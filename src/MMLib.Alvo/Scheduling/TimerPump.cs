using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;

using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Fires due timers: claims them under a one-minute lease and fires each through <see cref="ITimerStore.FireAsync"/>, whose
/// fenced delete and outbox append are one commit — in <see cref="IAlvoEventPump.DrainAsync"/> rounds and in a background loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fired event is rebuilt, never replayed.</b> Only the stored data is read back from the row's payload; the actor is
/// the system in the timer's scope, the subject <c>timers/&lt;key&gt;</c>, and the provenance the stored one one hop deeper —
/// the correlation and causation of the flow that armed it, at its depth + 1. Fan-out, not this pump, refuses an event past
/// the chain-depth limit, so a timer that re-arms itself ends as a dead delivery.
/// </para>
/// <para>
/// <b>A row that cannot be built</b> — a malformed payload or provenance, a scope that is not a tenant id, a type the custom
/// door now refuses — is logged once and cancelled at the generation its claim read, never thrown every round. A corrupted
/// provenance is not read as none: that would restart a loop's chain depth. A fire the store fails is logged and left for its lease
/// to return it.
/// </para>
/// <para>
/// The background loop keeps the delivery dispatcher's gates (<see cref="DispatcherGates"/>), its poll interval and its
/// backoff; with no timer store registered it logs once and stands down, and a manual round answers an empty result.
/// </para>
/// </remarks>
/// <param name="store">The timer store, or <see langword="null"/> when no provider registered one.</param>
/// <param name="boot">The readiness signal the loop waits on.</param>
/// <param name="catalogs">The primed catalog the gates require.</param>
/// <param name="events">The dispatch mode, poll interval and batch size, shared with the other dispatchers.</param>
/// <param name="time">The clock the fired event is stamped with and the idle wait is measured on.</param>
/// <param name="logger">Where unbuildable rows, failed fires and a stopped loop are recorded.</param>
internal sealed class TimerPump(
    ITimerStore? store,
    AlvoBootState boot,
    IPolicyCatalogProvider catalogs,
    IOptions<AlvoEventOptions> events,
    TimeProvider time,
    ILogger<TimerPump> logger) : BackgroundService
{
    /// <summary>How long a claim holds a timer before another claimant may take it back.</summary>
    internal static TimeSpan Lease { get; } = TimeSpan.FromMinutes(1);

    private bool _storeReady;

    /// <summary>One round on the caller's schedule, behind the manual round's gates, which it checks itself.</summary>
    /// <exception cref="InvalidOperationException">A gate is shut; nothing was claimed.</exception>
    internal async Task<AlvoDrainResult> ManualRoundAsync(CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return Quiet;
        }

        DispatcherGates.EnsureManual(events.Value, boot, catalogs);
        return await RoundAsync(store, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await PumpUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            TimerLog.TimerPumpStopped(logger, TypeOf(failure));
        }
    }

    private static AlvoDrainResult Quiet => new() { Claimed = 0, Failed = 0, Subscribers = [] };

    private async Task PumpUntilStoppedAsync(CancellationToken stop)
    {
        if (store is null)
        {
            TimerLog.TimerStoreAbsent(logger);
            return;
        }

        if (!await DispatcherGates.OpenAsync(
            events.Value, boot, catalogs, () => TimerLog.TimerPumpManual(logger), () => TimerLog.TimerPumpStoodDownUnprimed(logger), stop)
            .ConfigureAwait(false))
        {
            return;
        }

        var failing = TimeSpan.Zero;
        while (!stop.IsCancellationRequested)
        {
            failing = await TickAsync(store, failing, stop).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one round; idles on the poll interval when it fired nothing, and waits a growing backoff when it failed.</summary>
    private async Task<TimeSpan> TickAsync(ITimerStore timers, TimeSpan failing, CancellationToken stop)
    {
        try
        {
            if ((await RoundAsync(timers, stop).ConfigureAwait(false)).Claimed == 0)
            {
                await Task.Delay(events.Value.PollInterval, time, stop).ConfigureAwait(false);
            }

            return TimeSpan.Zero;
        }
        catch (Exception failure) when (!stop.IsCancellationRequested)
        {
            var wait = OutboxDispatcher.NextBackoff(failing, events.Value.PollInterval);
            TimerLog.TimerPumpRoundFailed(logger, TypeOf(failure), wait);
            await Task.Delay(wait, time, stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            return wait;
        }
    }

    private async Task<AlvoDrainResult> RoundAsync(ITimerStore timers, CancellationToken ct)
    {
        await EnsureStoreAsync(timers, ct).ConfigureAwait(false);
        var claimed = await timers.ClaimTimersAsync(events.Value.BatchSize, Lease, ct).ConfigureAwait(false);
        var outcomes = new List<bool?>(claimed.Count);
        foreach (var timer in claimed)
        {
            outcomes.Add(await FireOneAsync(timers, timer, ct).ConfigureAwait(false));
        }

        return new AlvoDrainResult
        {
            Claimed = outcomes.Count(outcome => outcome is true),
            Failed = outcomes.Count(outcome => outcome is null),
            Subscribers = [],
        };
    }

    /// <summary>Fires one claimed timer, or cancels it when its row cannot be built.</summary>
    /// <returns><see langword="true"/> when it fired, <see langword="false"/> when it was moved, cancelled or unbuildable, <see langword="null"/> when the store failed.</returns>
    private async Task<bool?> FireOneAsync(ITimerStore timers, ClaimedTimer timer, CancellationToken ct)
    {
        if (EventOf(timer) is not { } fired)
        {
            await timers.CancelAsync(timer.Scope, timer.Key, timer.Generation, ct).ConfigureAwait(false);
            return false;
        }

        try
        {
            return await timers.FireAsync(timer.Scope, timer.Key, timer.Generation, fired, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            TimerLog.TimerFireFailed(logger, timer.Scope, timer.Key, TypeOf(failure));
            return null;
        }
    }

    private AlvoCustomEvent? EventOf(ClaimedTimer timer)
    {
        try
        {
            var subject = $"timers/{timer.Key}";
            var data = AlvoEventJson.Read(timer.Payload).Data.Record?.Values;
            AlvoCustomEnvelope.Ensure(timer.MessageType, subject, data);
            return AlvoCustomEnvelope.Build(timer.MessageType, subject, data, FiringContext(timer), time.GetUtcNow());
        }
        catch (Exception failure) when (failure is ArgumentException or JsonException or FormatException or InvalidOperationException)
        {
            TimerLog.TimerUnbuildable(logger, timer.Scope, timer.Key, TypeOf(failure));
            return null;
        }
    }

    private static AlvoContext FiringContext(ClaimedTimer timer)
    {
        var stored = TimerProvenance.TryRead(timer.Provenance)
            ?? throw new FormatException("The timer's stored provenance is not one Alvo wrote.");

        return AlvoContext.System(TenantOf(timer.Scope)) with { Provenance = stored with { Depth = stored.Depth + 1 } };
    }

    private static TenantId? TenantOf(string scope) =>
        scope.Length == 0 ? null : TenantId.Parse(scope, CultureInfo.InvariantCulture);

    /// <summary>Brings the store up once; the loop and the manual rounds never run in one process together, so no lock.</summary>
    private async Task EnsureStoreAsync(ITimerStore timers, CancellationToken ct)
    {
        if (!_storeReady)
        {
            await timers.EnsureAsync(ct).ConfigureAwait(false);
            _storeReady = true;
        }
    }

    private static string TypeOf(Exception failure) => failure.GetType().FullName ?? failure.GetType().Name;
}
