using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Claims and runs event-handler deliveries: the lane heads of the handlers this process registers, at most
/// <see cref="AlvoDeliveryOptions.MaxConcurrency"/> at once (spec 2026-10-14 §4.8).
/// </summary>
/// <remarks>
/// <para>
/// It keeps the outbox dispatcher's gates — events enabled, the background mode, a <c>Ready</c> boot with a primed catalog —
/// for the same reason: a pump that runs before the schema is primed has nothing to bind a handler to. Nothing escapes
/// <see cref="ExecuteAsync"/>; a delivery that fails is the delivery's outcome, not the loop's.
/// </para>
/// <para>
/// <b>A failed round does not end the loop</b>, exactly as for <see cref="OutboxDispatcher"/>: a claim the store refuses —
/// an outage, a restarting database, a deadlock between two claimants — is logged
/// (<see cref="DeliveryLog.DeliveryDispatcherRoundFailed"/>, the exception's type name only) and the next round waits the
/// outbox's backoff (<see cref="OutboxDispatcher.NextBackoff"/>: the poll interval, doubling up to a minute, reset by a
/// round that succeeds). A delivery it could not answer for returns when its lease ends.
/// </para>
/// <para>
/// <b>The lease is the batch's</b>: one claim takes a batch under one lease of the longest registered timeout +
/// <see cref="DeliveryRunner.LeaseGrace"/> + <see cref="DeliveryRunner.SettleGrace"/>, never shorter than any row needs, so
/// the runner's answer — which can come the timeout and the settle margin after the attempt started — lands inside it.
/// </para>
/// <para>
/// <b>No claimed delivery waits for a slot.</b> The loop claims only free slots, and a manual round claims at most
/// <see cref="AlvoDeliveryOptions.MaxConcurrency"/> deliveries at a time, runs them and claims again, so no lease runs
/// while its delivery queues. A handler abandoned past its timeout holds no slot: the runner has answered for it.
/// </para>
/// <para>
/// <b>A handler that leaves abandoned invocations behind stands down.</b> Abandoned code keeps running — .NET cannot abort
/// it — so the claim leaves out every handler with <see cref="AlvoDeliveryOptions.MaxConcurrency"/> abandoned invocations
/// still alive (<see cref="AbandonedHandlers"/>), in the loop and in a manual round alike, and takes it back as soon as one
/// of them ends. A handler that blocks its thread and ignores its token therefore strands a bounded number of thread-pool
/// threads, not one per delivery; the other handlers keep flowing. Abandoned invocations are also counted
/// (<see cref="AlvoDeliveryMetrics.AbandonedName"/>).
/// </para>
/// <para>
/// <b>A shutdown waits for the runner, never for a handler.</b> The loop always awaits its in-flight deliveries before it
/// ends — also when the stop lands mid-claim — so an answer already being written is not raced against a disposed store.
/// On a stop the runner cancels the handler and answers nothing; the lease returns the delivery.
/// </para>
/// <para>
/// <b>The claimant is per instance</b> (<c>{machine}:{pid}:{instance}</c>): two dispatchers in one process — two test hosts
/// over one database — are two claimants, and completion is fenced on the attempt count besides, so a reclaimed lease's
/// late finisher changes nothing whoever it is.
/// </para>
/// </remarks>
/// <param name="store">The delivery queue: claim under a lease; the runner answers for each attempt.</param>
/// <param name="boot">The readiness signal the loop waits on before it claims anything.</param>
/// <param name="catalogs">The primed policy catalog the registered handlers are bound to.</param>
/// <param name="binder">Binds the registered handlers to the current catalog.</param>
/// <param name="runner">Runs one claimed delivery and answers for its attempt.</param>
/// <param name="abandoned">The abandoned invocations still running, per handler; a handler at the cap is not claimed for.</param>
/// <param name="events">The dispatch mode, poll interval and batch size, shared with the outbox dispatcher.</param>
/// <param name="deliveries">How many deliveries may run at once.</param>
/// <param name="time">The clock the idle wait is measured on.</param>
/// <param name="logger">Where a failed round, a delivery nothing answered for and a stopped loop are recorded.</param>
internal sealed class DeliveryDispatcher(
    IDeliveryStore store,
    AlvoBootState boot,
    IPolicyCatalogProvider catalogs,
    ExtensionBinder binder,
    DeliveryRunner runner,
    AbandonedHandlers abandoned,
    IOptions<AlvoEventOptions> events,
    IOptions<AlvoDeliveryOptions> deliveries,
    TimeProvider time,
    ILogger<DeliveryDispatcher> logger) : BackgroundService
{
    private readonly string _claimant = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private bool _storeReady;

    /// <summary>
    /// One round on the caller's schedule, behind the loop's gates (the manual mode, events on, a Ready boot with a primed
    /// catalog), which it checks itself rather than trusting the order of the pump's steps. It claims at most
    /// <see cref="AlvoDeliveryOptions.MaxConcurrency"/> lane heads at a time, runs them, and claims again — until nothing is
    /// due or <see cref="AlvoEventOptions.BatchSize"/> deliveries were claimed in the round.
    /// </summary>
    /// <exception cref="InvalidOperationException">A gate is shut; nothing was claimed.</exception>
    /// <exception cref="OperationCanceledException">The round was cancelled; what it claimed is left to its lease.</exception>
    internal async Task<AlvoDrainResult> ManualRoundAsync(CancellationToken cancellationToken)
    {
        var catalog = EnsureManualGates();
        await EnsureStoreAsync(cancellationToken).ConfigureAwait(false);
        var bindings = binder.For(catalog);
        var round = new DrainRound();
        var claimedInRound = 0;
        while (claimedInRound < events.Value.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slots = Math.Min(deliveries.Value.MaxConcurrency, events.Value.BatchSize - claimedInRound);
            var claimed = await ClaimAsync(bindings, slots, cancellationToken).ConfigureAwait(false);
            if (claimed.Count == 0)
            {
                break;
            }

            claimedInRound += claimed.Count;
            await Task.WhenAll(claimed.Select(delivery => RunContainedAsync(delivery, bindings, round, cancellationToken))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return round.Result(claimedInRound);
    }

    private PolicyCatalog EnsureManualGates()
    {
        var settings = events.Value;
        if (settings.DispatchMode is not AlvoEventDispatchMode.Manual || !settings.Enabled)
        {
            throw new InvalidOperationException(
                "A manual delivery round was refused before anything was claimed: the dispatch mode is not manual, or events "
                + $"are off. Configured: mode '{settings.DispatchMode}', enabled {settings.Enabled}.");
        }

        return boot.Phase is AlvoBootPhase.Ready && catalogs.Current is { } catalog
            ? catalog
            : throw new InvalidOperationException(
                "A manual delivery round was refused before anything was claimed: the boot has not primed a schema, so no "
                + "handler can be bound. Start the host, let its boot finish, and apply a descriptor first.");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The first arm is the host stopping: whatever a stop interrupts — a claim cancelled mid-statement, a store already
    /// going away — is the shutdown, not a failure, and is not logged. The other arm is the backstop for a failure outside
    /// the rounds — the boot wait itself — since every round contains its own failure.
    /// </remarks>
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
            DeliveryLog.DeliveryDispatcherStopped(logger, TypeOf(failure));
        }
    }

    private async Task PumpUntilStoppedAsync(CancellationToken stop)
    {
        if (!await GatesOpenAsync(stop).ConfigureAwait(false))
        {
            return;
        }

        var running = new List<Task>();
        var failing = TimeSpan.Zero;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                running.RemoveAll(task => task.IsCompleted);
                failing = await TickAsync(running, failing, stop).ConfigureAwait(false);
            }
        }
        finally
        {
            await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Claims the free slots and starts what it claimed; idles on the poll interval or the first finishing delivery when it
    /// claimed nothing, and waits a growing backoff when the round failed.
    /// </summary>
    /// <returns>The backoff the next round starts from; <see cref="TimeSpan.Zero"/> after a round that succeeded.</returns>
    private async Task<TimeSpan> TickAsync(List<Task> running, TimeSpan failing, CancellationToken stop)
    {
        try
        {
            await EnsureStoreAsync(stop).ConfigureAwait(false);
            var bindings = binder.For(catalogs.Current!);
            var claimed = await ClaimAsync(bindings, deliveries.Value.MaxConcurrency - running.Count, stop).ConfigureAwait(false);
            running.AddRange(claimed.Select(delivery => RunContainedAsync(delivery, bindings, round: null, stop)));
            if (claimed.Count == 0)
            {
                await Task.WhenAny([Task.Delay(events.Value.PollInterval, time, stop), .. running]).ConfigureAwait(false);
            }

            return TimeSpan.Zero;
        }
        catch (Exception failure) when (!stop.IsCancellationRequested)
        {
            var wait = OutboxDispatcher.NextBackoff(failing, events.Value.PollInterval);
            DeliveryLog.DeliveryDispatcherRoundFailed(logger, TypeOf(failure), wait);
            await Task.Delay(wait, time, stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            return wait;
        }
    }

    private async Task<bool> GatesOpenAsync(CancellationToken stop)
    {
        if (!events.Value.Enabled)
        {
            return false;
        }

        if (events.Value.DispatchMode is AlvoEventDispatchMode.Manual)
        {
            DeliveryLog.DeliveryDispatcherManual(logger);
            return false;
        }

        if (await boot.SettledAsync(stop).ConfigureAwait(false) is not AlvoBootPhase.Ready)
        {
            return false;
        }

        if (catalogs.Current is null)
        {
            DeliveryLog.DeliveryDispatcherStoodDownUnprimed(logger);
            return false;
        }

        return true;
    }

    /// <summary>Brings the store up once; the loop and the manual rounds never run in one process together, so no lock.</summary>
    private async Task EnsureStoreAsync(CancellationToken ct)
    {
        if (!_storeReady)
        {
            await store.EnsureAsync(ct).ConfigureAwait(false);
            _storeReady = true;
        }
    }

    /// <summary>Claims up to <paramref name="batchSize"/> lane heads of the bound handlers that are not stood down.</summary>
    private async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(ExtensionBindings bindings, int batchSize, CancellationToken ct)
    {
        var claimable = bindings.Handlers.Where(handler => !abandoned.StoodDown(handler.Id)).ToList();
        return batchSize <= 0 || claimable.Count == 0
            ? []
            : await store.ClaimAsync(ClaimOf(bindings, claimable, batchSize), ct).ConfigureAwait(false);
    }

    private DeliveryClaim ClaimOf(ExtensionBindings bindings, List<BoundHandler> claimable, int batchSize) => new()
    {
        Claimant = _claimant,
        Ordered = [.. claimable.Where(handler => handler.Settings.Ordering is AlvoOrdering.PerRecord).Select(handler => handler.Id)],
        Unordered = [.. claimable.Where(handler => handler.Settings.Ordering is AlvoOrdering.None).Select(handler => handler.Id)],
        BatchSize = batchSize,
        Lease = bindings.LongestTimeout + DeliveryRunner.LeaseGrace + runner.SettleGrace,
        OutboxMaxAttempts = events.Value.MaxAttempts,
        OutboxClaimLease = events.Value.ClaimLease,
    };

    /// <summary>Runs one delivery, containing whatever the runner throws so neither its round nor the loop is affected.</summary>
    private async Task RunContainedAsync(ClaimedDelivery delivery, ExtensionBindings bindings, DrainRound? round, CancellationToken stop)
    {
        if (bindings.Handler(delivery.Subscriber) is not { } handler)
        {
            return;
        }

        round?.Ran(delivery.Subscriber);
        try
        {
            var outcome = await runner.RunAsync(delivery, handler, _claimant, stop).ConfigureAwait(false);
            if (outcome is DeliveryRunOutcome.Failed or DeliveryRunOutcome.Dead)
            {
                round?.Failed();
            }
        }
        catch (Exception failure) when (!stop.IsCancellationRequested)
        {
            round?.Failed();
            DeliveryLog.DeliveryRunFailed(logger, delivery.Id, delivery.Subscriber, TypeOf(failure));
        }
    }

    private static string TypeOf(Exception failure) => failure.GetType().FullName ?? failure.GetType().Name;
}
