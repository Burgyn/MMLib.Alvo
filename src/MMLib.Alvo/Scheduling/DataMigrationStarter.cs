using Microsoft.Extensions.Logging;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;

using System.Collections.Concurrent;
using System.Text.Json;

namespace MMLib.Alvo.Scheduling;

/// <summary>What one pass of <see cref="DataMigrationStarter.StartDueAsync"/> did: migrations started, and starts that failed.</summary>
/// <param name="Started">How many migrations this pass started.</param>
/// <param name="Failed">How many starts failed and wait for their retry.</param>
internal readonly record struct MigrationRound(int Started, int Failed)
{
    internal static MigrationRound None => default;
}

/// <summary>
/// Starts each bound data migration once, when the applied revision first reaches the one it waits for: marks it started and
/// appends its framework events in one commit through <see cref="ITimerStore.StartMigrationAsync"/> — one event per tenant when
/// that revision's schema has a scoped entity, else one. A step of the schedule pump's round.
/// </summary>
/// <remarks>
/// <para>
/// <b>Started is permanent.</b> A rollback appends a new, higher revision and the started row is never removed, so neither a
/// rollback nor a re-apply nor a restart starts a migration again. The started ids are read once per process and kept.
/// </para>
/// <para>
/// <b>Per tenant is decided by the revision it starts at</b>, read from the store with that revision — never by this process's
/// catalog, which a replica that did not make the apply still holds at an older revision. So is whether the self-service
/// registry lists the tenants (<see cref="TenantCatalogs"/>): only while that revision declares <c>tenancy.selfService</c>.
/// </para>
/// <para>
/// <b>A schema scoped after the boot holds the migration.</b> The boot refuses an unstarted migration on a scoped schema without
/// a tenant list; an apply that first scopes the schema is not refused, so the run is skipped instead —
/// logged once, not marked — and starts on the first round of a process that has a tenant list.
/// </para>
/// <para>
/// <b>A failed start waits its own backoff</b> — one minute, doubling to fifteen — and does not stop the other migrations; a
/// scoped schema with no tenants is marked started with no runs, and says so.
/// </para>
/// </remarks>
/// <param name="runs">Mints the migration's events, and knows whether a tenant list exists.</param>
/// <param name="applied">The applied descriptor, or <see langword="null"/> when no provider registered one.</param>
/// <param name="time">The clock a failed start's retry is measured on.</param>
/// <param name="logger">Where a held, empty or failed start is recorded.</param>
internal sealed class DataMigrationStarter(
    ScheduleRuns runs, IAppliedSchemaStore? applied, TimeProvider time, ILogger<DataMigrationStarter> logger)
{
    /// <summary>How long a failed start waits before its first retry.</summary>
    internal static TimeSpan RetryFloor { get; } = TimeSpan.FromMinutes(1);

    /// <summary>The longest a failed start waits between retries.</summary>
    internal static TimeSpan RetryCeiling { get; } = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, bool> _started = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Retry> _retries = new(StringComparer.Ordinal);
    private volatile bool _loaded;

    /// <summary>Starts every bound migration of <paramref name="project"/> whose revision has been reached and whose retry is due.</summary>
    /// <param name="store">The timer store that marks a start and appends its events.</param>
    /// <param name="bindings">The extensions bound to the current catalog.</param>
    /// <param name="project">The project whose applied revision is read.</param>
    /// <param name="ct">A token to cancel the round.</param>
    /// <returns>How many migrations this call started, and how many starts failed.</returns>
    internal async Task<MigrationRound> StartDueAsync(ITimerStore store, ExtensionBindings bindings, string project, CancellationToken ct)
    {
        var pending = await PendingAsync(store, bindings, ct).ConfigureAwait(false);
        if (pending.Count == 0 || applied is null || await applied.GetCurrentAsync(project, ct).ConfigureAwait(false) is not { } current)
        {
            return MigrationRound.None;
        }

        var perTenant = ScheduleChecks.IsScoped(current.Schema);
        var selfService = perTenant && !runs.ListsTenants(selfService: false) && DeclaresSelfService(current.DescriptorJson);
        var round = MigrationRound.None;
        foreach (var migration in pending.Where(migration => current.Revision >= migration.Job!.AfterRevision))
        {
            var start = new MigrationStart(current.Revision, perTenant && !migration.Job!.SpansTenants, selfService);
            round = Tally(round, await TryStartAsync(store, migration, start, ct).ConfigureAwait(false));
        }

        return round;
    }

    /// <summary>Whether the applied descriptor declares <c>tenancy.selfService</c>; one that cannot be read lists nothing (fail closed).</summary>
    private static bool DeclaresSelfService(string descriptorJson)
    {
        try
        {
            return AlvoDescriptor.Parse(descriptorJson).Tenancy?.SelfService is not null;
        }
        catch (Exception unreadable) when (unreadable is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static MigrationRound Tally(MigrationRound round, bool? outcome) => outcome switch
    {
        true => round with { Started = round.Started + 1 },
        null => round with { Failed = round.Failed + 1 },
        _ => round,
    };

    private async Task<List<HandlerRegistration>> PendingAsync(ITimerStore store, ExtensionBindings bindings, CancellationToken ct)
    {
        List<HandlerRegistration> bound = [.. bindings.Handlers.Select(handler => handler.Registration).Where(handler => handler.Job is { IsMigration: true })];
        if (bound.Count > 0 && !_loaded)
        {
            foreach (var id in await store.StartedMigrationsAsync(ct).ConfigureAwait(false))
            {
                _started[id] = true;
            }

            _loaded = true;
        }

        var now = time.GetUtcNow();
        return [.. bound.Where(handler => !_started.ContainsKey(handler.Id) && !(_retries.TryGetValue(handler.Id, out var retry) && retry.At > now))];
    }

    /// <summary>Starts one migration, holds it for a missing tenant list, or records its failure and its retry.</summary>
    /// <returns><see langword="true"/> when it started, <see langword="false"/> when it was held or another replica started it, <see langword="null"/> when it failed.</returns>
    private async Task<bool?> TryStartAsync(ITimerStore store, HandlerRegistration migration, MigrationStart start, CancellationToken ct)
    {
        if (start.PerTenant && !runs.ListsTenants(start.SelfService))
        {
            Hold(migration.Id, start.Revision);
            return false;
        }

        try
        {
            var started = await StartAsync(store, migration, start, ct).ConfigureAwait(false);
            _started[migration.Id] = true;
            _retries.TryRemove(migration.Id, out _);

            return started;
        }
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            Failed(migration.Id, start.Revision, failure);
            return null;
        }
    }

    private async Task<bool> StartAsync(ITimerStore store, HandlerRegistration migration, MigrationStart start, CancellationToken ct)
    {
        var events = await runs.MigrationEventsAsync(migration, start.Revision, start.PerTenant, start.SelfService, ct).ConfigureAwait(false);
        var started = await store.StartMigrationAsync(migration.Id, start.Revision, events, ct).ConfigureAwait(false);
        if (started && events.Count == 0)
        {
            SchedulePumpLog.MigrationStartedWithNoTenants(logger, migration.Id, start.Revision);
        }

        return started;
    }

    private void Hold(string id, int revision)
    {
        if (_held.TryAdd(id, true))
        {
            SchedulePumpLog.TenantCatalogMissing(logger, id, revision);
        }
    }

    private void Failed(string id, int revision, Exception failure)
    {
        var wait = _retries.TryGetValue(id, out var previous) ? Next(previous.Wait) : RetryFloor;
        _retries[id] = new Retry(time.GetUtcNow() + wait, wait);
        SchedulePumpLog.MigrationStartFailed(logger, id, revision, failure.GetType().FullName ?? failure.GetType().Name, wait);
    }

    private static TimeSpan Next(TimeSpan previous) => previous * 2 < RetryCeiling ? previous * 2 : RetryCeiling;

    private readonly record struct Retry(DateTimeOffset At, TimeSpan Wait);

    /// <summary>One migration's start: the revision it starts at, whether each tenant gets a run, and whether that revision declares <c>selfService</c>.</summary>
    private readonly record struct MigrationStart(int Revision, bool PerTenant, bool SelfService);
}
