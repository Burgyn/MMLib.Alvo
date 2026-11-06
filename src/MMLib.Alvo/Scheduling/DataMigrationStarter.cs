using Microsoft.Extensions.Logging;

using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Starts each bound data migration once, when the applied revision first reaches the one it waits for: marks it started and
/// appends its framework events in one commit through <see cref="ITimerStore.StartMigrationAsync"/> — one event per tenant on a
/// scoped schema, else one. A step of the schedule pump's round.
/// </summary>
/// <remarks>
/// <para>
/// <b>Started is permanent.</b> A rollback appends a new, higher revision and the started row is never removed, so neither a
/// rollback nor a re-apply nor a restart starts a migration again. The started ids are read once per process and kept.
/// </para>
/// <para>
/// <b>A schema scoped after the boot holds the migration.</b> The boot refuses a migration on a scoped schema without an
/// <see cref="IAlvoTenantCatalog"/>; an apply that first scopes the schema is not refused, so the run is skipped instead —
/// logged once, not marked — and starts on the first round of a process that has a tenant list.
/// </para>
/// </remarks>
/// <param name="runs">Mints the migration's events, and knows whether a tenant list exists.</param>
/// <param name="applied">The applied descriptor's revision, or <see langword="null"/> when no provider registered one.</param>
/// <param name="catalogs">The catalog whose schema decides whether the run is per tenant.</param>
/// <param name="logger">Where a held or failed start is recorded.</param>
internal sealed class DataMigrationStarter(
    ScheduleRuns runs, IAppliedSchemaStore? applied, IPolicyCatalogProvider catalogs, ILogger<DataMigrationStarter> logger)
{
    private readonly ConcurrentDictionary<string, bool> _started = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _held = new(StringComparer.Ordinal);
    private volatile bool _loaded;

    /// <summary>Starts every bound migration of <paramref name="project"/> whose revision has been reached.</summary>
    /// <param name="store">The timer store that marks a start and appends its events.</param>
    /// <param name="bindings">The extensions bound to the current catalog.</param>
    /// <param name="project">The project whose applied revision is read.</param>
    /// <param name="ct">A token to cancel the round.</param>
    /// <returns>How many migrations this call started.</returns>
    internal async Task<int> StartDueAsync(ITimerStore store, ExtensionBindings bindings, string project, CancellationToken ct)
    {
        var pending = await PendingAsync(store, bindings, ct).ConfigureAwait(false);
        if (pending.Count == 0 || applied is null || catalogs.Current is not { } catalog
            || await applied.GetCurrentAsync(project, ct).ConfigureAwait(false) is not { } current)
        {
            return 0;
        }

        var started = 0;
        foreach (var migration in pending.Where(migration => current.Revision >= migration.Job!.AfterRevision))
        {
            started += await TryStartAsync(store, migration, current.Revision, ScheduleChecks.IsScoped(catalog.Schema), ct).ConfigureAwait(false) ? 1 : 0;
        }

        return started;
    }

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

        return [.. bound.Where(handler => !_started.ContainsKey(handler.Id))];
    }

    private async Task<bool> TryStartAsync(ITimerStore store, HandlerRegistration migration, int revision, bool perTenant, CancellationToken ct)
    {
        if (perTenant && !runs.HasTenantCatalog)
        {
            if (_held.TryAdd(migration.Id, true))
            {
                SchedulePumpLog.TenantCatalogMissing(logger, migration.Id, revision);
            }

            return false;
        }

        var events = await runs.MigrationEventsAsync(migration, revision, perTenant, ct).ConfigureAwait(false);
        var started = await store.StartMigrationAsync(migration.Id, revision, events, ct).ConfigureAwait(false);
        _started[migration.Id] = true;

        return started;
    }
}
