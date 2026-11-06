using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Runs cron schedules: claims the occurrences now due under a one-minute lease and advances each through
/// <see cref="ITimerStore.AdvanceScheduleAsync"/>, whose fenced advance and outbox append of the run's framework events are one
/// commit — in <see cref="IAlvoEventPump.DrainAsync"/> rounds and in a background loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>The schedules this process binds are synced when the host starts</b> (<see cref="StartAsync"/>), and again by the first
/// round over each newly applied catalog — so a cron rule an apply adds is scheduled without a restart — a new id
/// gets its first occurrence, a changed cron or zone moves its next run, and an id declared with <c>AddRetiredExtension</c> is
/// removed. A schedule this process does not register is neither removed nor claimed: processes sharing one database — a web
/// host and a worker — register different schedules. A boot that had no primed catalog at start leaves the sync to the first
/// round that has one; a process with nothing to sync writes nothing.
/// </para>
/// <para>
/// <b>A missed occurrence runs once.</b> An occurrence that fell due while nothing ran is claimed once and the schedule resumes
/// from the later of now and that occurrence, so a host down for a week runs a daily job once, not seven times.
/// </para>
/// <para>
/// <b>Each round then starts the data migrations whose revision has been reached</b> (<see cref="DataMigrationStarter"/>); a
/// failed start is logged and waits its own backoff, without holding the others back.
/// </para>
/// <para>
/// <b>A process claims only the schedules it binds</b>, so an orphaned row runs nowhere and a replica that does not register a
/// schedule never holds its lease. A run whose
/// events cannot be minted — the tenant list failed — or whose advance the store failed is logged and returns with its lease.
/// </para>
/// </remarks>
/// <param name="store">The timer store, or <see langword="null"/> when no provider registered one.</param>
/// <param name="binder">Binds the registered schedules to the current catalog.</param>
/// <param name="registry">The registrations, read for the ids this process declares retired.</param>
/// <param name="runs">Mints the framework events of one occurrence.</param>
/// <param name="migrations">Starts the data migrations whose revision has been reached.</param>
/// <param name="boot">The readiness signal the loop and the sync wait on.</param>
/// <param name="catalogs">The primed catalog the gates require.</param>
/// <param name="events">The dispatch mode, poll interval and events switch, shared with the other dispatchers.</param>
/// <param name="time">The clock occurrences are computed on and the idle wait is measured on.</param>
/// <param name="logger">Where failed runs, a failed sync and a stopped loop are recorded.</param>
internal sealed class SchedulePump(
    ITimerStore? store,
    ExtensionBinder binder,
    ExtensionRegistry registry,
    ScheduleRuns runs,
    DataMigrationStarter migrations,
    AlvoBootState boot,
    IPolicyCatalogProvider catalogs,
    IOptions<AlvoEventOptions> events,
    TimeProvider time,
    ILogger<SchedulePump> logger) : BackgroundService
{
    /// <summary>How long a claim holds an occurrence before another claimant may take it back.</summary>
    internal static TimeSpan Lease { get; } = TimeSpan.FromMinutes(1);

    /// <summary>This pump's fence: per instance, so two pumps in one process never advance each other's claims.</summary>
    private readonly string _claimant = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private PolicyCatalog? _syncedFor;

    /// <summary>One round on the caller's schedule, behind the manual round's gates, which it checks itself.</summary>
    /// <exception cref="InvalidOperationException">A gate is shut; nothing was claimed.</exception>
    internal async Task<AlvoDrainResult> ManualRoundAsync(CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return Quiet;
        }

        var catalog = EnsureManualGates();
        return await RoundAsync(store, catalog, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The boot has run by now — it is a lifecycle service that starts before every hosted service — so its phase is read, never
    /// waited for: a host whose boot has not made it ready leaves the sync to the first round.
    /// </remarks>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (store is not null && boot.Phase is AlvoBootPhase.Ready && catalogs.Current is { } catalog)
        {
            await TrySyncAsync(store, catalog, cancellationToken).ConfigureAwait(false);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
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
            SchedulePumpLog.SchedulePumpStopped(logger, TypeOf(failure));
        }
    }

    private static AlvoDrainResult Quiet => new() { Claimed = 0, Failed = 0, Subscribers = [] };

    private async Task PumpUntilStoppedAsync(CancellationToken stop)
    {
        if (store is null
            || !await DispatcherGates.OpenAsync(events.Value, boot, catalogs, () => { }, () => { }, stop).ConfigureAwait(false))
        {
            return;
        }

        var failing = TimeSpan.Zero;
        while (!stop.IsCancellationRequested)
        {
            failing = await TickAsync(store, failing, stop).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one round; idles on the poll interval when it advanced nothing, and waits a growing backoff when it failed.</summary>
    private async Task<TimeSpan> TickAsync(ITimerStore schedules, TimeSpan failing, CancellationToken stop)
    {
        try
        {
            if ((await RoundAsync(schedules, catalogs.Current!, stop).ConfigureAwait(false)).Claimed == 0)
            {
                await Task.Delay(events.Value.PollInterval, time, stop).ConfigureAwait(false);
            }

            return TimeSpan.Zero;
        }
        catch (Exception failure) when (!stop.IsCancellationRequested)
        {
            var wait = OutboxDispatcher.NextBackoff(failing, events.Value.PollInterval);
            SchedulePumpLog.SchedulePumpRoundFailed(logger, TypeOf(failure), wait);
            await Task.Delay(wait, time, stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            return wait;
        }
    }

    private async Task<AlvoDrainResult> RoundAsync(ITimerStore schedules, PolicyCatalog catalog, CancellationToken ct)
    {
        if (!ReferenceEquals(Volatile.Read(ref _syncedFor), catalog))
        {
            await SyncAsync(schedules, catalog, ct).ConfigureAwait(false);
        }

        var bindings = binder.For(catalog);
        var claimed = await schedules.ClaimSchedulesAsync(_claimant, ScheduleIds(bindings), Lease, ct).ConfigureAwait(false);
        var outcomes = new List<bool?>(claimed.Count);
        foreach (var due in claimed)
        {
            outcomes.Add(await RunOneAsync(schedules, bindings, due, ct).ConfigureAwait(false));
        }

        var started = await StartMigrationsAsync(schedules, bindings, ct).ConfigureAwait(false);

        return new AlvoDrainResult
        {
            Claimed = outcomes.Count(outcome => outcome is true) + started.Started,
            Failed = outcomes.Count(outcome => outcome is null) + started.Failed,
            Subscribers = [],
        };
    }

    /// <summary>Starts the due data migrations of every project this process serves; a failed read is logged, not thrown.</summary>
    private async Task<MigrationRound> StartMigrationsAsync(ITimerStore schedules, ExtensionBindings bindings, CancellationToken ct)
    {
        var round = MigrationRound.None;
        try
        {
            foreach (var project in boot.Projects.Where(entry => entry.Value is AlvoBootPhase.Ready).Select(entry => entry.Key))
            {
                var started = await migrations.StartDueAsync(schedules, bindings, project, ct).ConfigureAwait(false);
                round = new(round.Started + started.Started, round.Failed + started.Failed);
            }

            return round;
        }
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            SchedulePumpLog.MigrationRoundFailed(logger, TypeOf(failure));
            return round with { Failed = round.Failed + 1 };
        }
    }

    /// <summary>Mints one claimed occurrence's events and advances the schedule past it, in one commit.</summary>
    /// <returns><see langword="true"/> when it advanced, <see langword="false"/> when it was left or fenced off, <see langword="null"/> when it failed.</returns>
    private async Task<bool?> RunOneAsync(ITimerStore schedules, ExtensionBindings bindings, ClaimedSchedule due, CancellationToken ct)
    {
        if (CronOf(bindings, due.Id) is not ({ } job, { } cron))
        {
            return false;
        }

        try
        {
            var produced = await runs.EventsAsync(job.Registration, due.ScheduledFor, manual: false, ct).ConfigureAwait(false);
            return cron.Next(Later(time.GetUtcNow(), due.ScheduledFor)) is { } next
                && await schedules.AdvanceScheduleAsync(Advance(due, next, produced), ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            SchedulePumpLog.ScheduleRunFailed(logger, due.Id, due.ScheduledFor, TypeOf(failure));
            return null;
        }
    }

    private ScheduleAdvance Advance(ClaimedSchedule due, DateTimeOffset next, IReadOnlyList<AlvoFrameworkEnvelope> produced) => new()
    {
        Id = due.Id,
        Claimant = _claimant,
        ScheduledFor = due.ScheduledFor,
        NextRunAt = next,
        Events = produced,
    };

    private (BoundHandler? Job, CronSchedule? Cron) CronOf(ExtensionBindings bindings, string id) =>
        bindings.Handler(id) is { Registration.Job: { IsSchedule: true, Cron: { } cron, TimeZone: { } zone } } job
        && CronSchedule.TryParse(cron, zone, time.GetUtcNow(), out var schedule, out _)
            ? (job, schedule)
            : (null, null);

    private static DateTimeOffset Later(DateTimeOffset now, DateTimeOffset scheduledFor) => now > scheduledFor ? now : scheduledFor;

    /// <summary>Syncs at start; a failure is logged and the first round tries again rather than refusing the host.</summary>
    private async Task TrySyncAsync(ITimerStore schedules, PolicyCatalog catalog, CancellationToken ct)
    {
        try
        {
            await SyncAsync(schedules, catalog, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            SchedulePumpLog.ScheduleSyncFailed(logger, TypeOf(failure));
        }
    }

    private async Task SyncAsync(ITimerStore schedules, PolicyCatalog catalog, CancellationToken ct)
    {
        var definitions = Definitions(binder.For(catalog));
        List<string> retired = [.. registry.Retired.Select(retirement => retirement.Id)];
        if (definitions.Count > 0 || retired.Count > 0)
        {
            await schedules.EnsureAsync(ct).ConfigureAwait(false);
            await schedules.SyncSchedulesAsync(definitions, retired, ct).ConfigureAwait(false);
        }

        Volatile.Write(ref _syncedFor, catalog);
    }

    private static List<string> ScheduleIds(ExtensionBindings bindings) =>
        [.. bindings.Handlers.Where(handler => handler.Registration.Job is { IsSchedule: true }).Select(handler => handler.Id)];

    private List<ScheduleDefinition> Definitions(ExtensionBindings bindings)
    {
        var now = time.GetUtcNow();
        var definitions = new List<ScheduleDefinition>();
        foreach (var job in bindings.Handlers.Where(handler => handler.Registration.Job is { IsSchedule: true }))
        {
            if (CronOf(bindings, job.Id) is ({ }, { } cron) && cron.Next(now) is { } next)
            {
                definitions.Add(Definition(job.Registration.Job!, job.Id, next));
            }
            else
            {
                SchedulePumpLog.ScheduleNeverFires(logger, job.Id);
            }
        }

        return definitions;
    }

    private static ScheduleDefinition Definition(JobSpec job, string id, DateTimeOffset next) =>
        new() { Id = id, Cron = job.Cron!, TimeZone = job.TimeZone!, NextRunAt = next };

    private PolicyCatalog EnsureManualGates()
    {
        var settings = events.Value;
        if (settings.DispatchMode is not AlvoEventDispatchMode.Manual || !settings.Enabled)
        {
            throw new InvalidOperationException(
                "A manual schedule round was refused before anything ran: the dispatch mode is not manual, or events are off. "
                + $"Configured: mode '{settings.DispatchMode}', enabled {settings.Enabled}.");
        }

        return boot.Phase is AlvoBootPhase.Ready && catalogs.Current is { } catalog
            ? catalog
            : throw new InvalidOperationException(
                "A manual schedule round was refused before anything ran: the boot has not primed a schema. Start the host, let "
                + "its boot finish, and apply a descriptor first.");
    }

    private static string TypeOf(Exception failure) => failure.GetType().FullName ?? failure.GetType().Name;
}
