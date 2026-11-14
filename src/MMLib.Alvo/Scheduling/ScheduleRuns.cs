using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tenants.SelfService;

using System.Globalization;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Mints the framework events of one schedule occurrence or data migration start: one per tenant the tenant list
/// (<see cref="TenantCatalogs"/>: the host's <see cref="IAlvoTenantCatalog"/>, else the self-service registry's) lists for an
/// <see cref="AlvoScheduleScope.EachTenant"/> schedule or a migration on a scoped schema, else one with no tenant — each as the
/// system in its tenant, under a correlation of its own at depth 0, whatever flow asked for it.
/// </summary>
internal sealed class ScheduleRuns
{
    internal const string ScheduleKey = "schedule";
    internal const string ManualKey = "manual";
    internal const string MigrationKey = "migration";

    private readonly Func<SelfServicePolicy?, IAlvoTenantCatalog?> _tenants;
    private readonly TimeProvider _time;

    /// <summary>Mints runs over the tenant list <paramref name="catalogs"/> resolves under the descriptor each run is judged by.</summary>
    /// <param name="catalogs">Resolves the tenant list; the boot refuses a per-tenant schedule wherever it resolves none.</param>
    /// <param name="time">The clock each event is stamped with.</param>
    internal ScheduleRuns(TenantCatalogs catalogs, TimeProvider time)
        : this(policy => policy is { } declared ? catalogs.For(declared) : catalogs.Current, time)
    {
    }

    /// <summary>Mints runs over one fixed tenant list, whatever the descriptor says.</summary>
    /// <param name="tenants">The tenant list, or <see langword="null"/> for none.</param>
    /// <param name="time">The clock each event is stamped with.</param>
    internal ScheduleRuns(IAlvoTenantCatalog? tenants, TimeProvider time)
        : this(_ => tenants, time)
    {
    }

    private ScheduleRuns(Func<SelfServicePolicy?, IAlvoTenantCatalog?> tenants, TimeProvider time)
    {
        _tenants = tenants;
        _time = time;
    }

    /// <summary>Whether a tenant list exists under a descriptor whose self-service policy is <paramref name="policy"/>.</summary>
    /// <param name="policy">The self-service policy of the descriptor a per-tenant run is judged by.</param>
    internal bool ListsTenants(SelfServicePolicy policy) => _tenants(policy) is not null;

    /// <summary>The events of <paramref name="job"/>'s run for <paramref name="scheduledFor"/>, in the order the tenants are listed.</summary>
    /// <remarks>A per-tenant run lists the tenants under the applied descriptor, the one its schedule is bound to.</remarks>
    /// <param name="job">A registered schedule.</param>
    /// <param name="scheduledFor">The occurrence; for a run started on demand, the moment it was started.</param>
    /// <param name="manual">Whether an operator started the run, rather than the cron.</param>
    /// <param name="ct">A token to cancel the tenant listing.</param>
    internal async Task<IReadOnlyList<AlvoFrameworkEnvelope>> EventsAsync(
        HandlerRegistration job, DateTimeOffset scheduledFor, bool manual, CancellationToken ct)
    {
        var type = ((NamedEvent)job.Source).Type;
        var payload = Payload(job.Id, scheduledFor, manual);
        var now = _time.GetUtcNow();
        var scopes = await ScopesAsync(job.Job!, ct).ConfigureAwait(false);

        return [.. scopes.Select(tenant => Mint(type, $"schedules/{job.Id}", payload, tenant, now))];
    }

    /// <summary>The events that start data migration <paramref name="job"/> at <paramref name="revision"/>, one per tenant when <paramref name="perTenant"/>.</summary>
    /// <param name="job">A registered data migration.</param>
    /// <param name="revision">The applied revision it starts at.</param>
    /// <param name="perTenant">Whether the schema has a scoped entity, so each tenant gets a run of its own.</param>
    /// <param name="policy">The self-service policy of the descriptor of <paramref name="revision"/>.</param>
    /// <param name="ct">A token to cancel the tenant listing.</param>
    internal async Task<IReadOnlyList<AlvoFrameworkEnvelope>> MigrationEventsAsync(
        HandlerRegistration job, int revision, bool perTenant, SelfServicePolicy policy, CancellationToken ct)
    {
        var type = ((NamedEvent)job.Source).Type;
        var now = _time.GetUtcNow();
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [MigrationKey] = job.Id,
            [JobInvocation.RevisionKey] = revision,
            [JobInvocation.ScheduledForKey] = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };
        IReadOnlyList<TenantId?> scopes = perTenant ? await TenantsAsync(policy, ct).ConfigureAwait(false) : [null];

        return [.. scopes.Select(tenant => Mint(type, $"migrations/{job.Id}", payload, tenant, now))];
    }

    private async Task<IReadOnlyList<TenantId?>> ScopesAsync(JobSpec job, CancellationToken ct) =>
        job.Scope is AlvoScheduleScope.EachTenant ? await TenantsAsync(null, ct).ConfigureAwait(false) : [null];

    /// <summary>The tenants listed under a descriptor whose self-service policy is <paramref name="policy"/> — the applied one when <see langword="null"/>.</summary>
    private async Task<IReadOnlyList<TenantId?>> TenantsAsync(SelfServicePolicy? policy, CancellationToken ct)
    {
        var listed = await (_tenants(policy) ?? throw NoTenantCatalog()).ListTenantsAsync(ct).ConfigureAwait(false);
        return [.. listed.Distinct().Select(tenant => (TenantId?)tenant)];
    }

    /// <summary>A fresh flow around the mint, so neither the depth nor the causation of whatever called it leaks into the run.</summary>
    private static AlvoFrameworkEnvelope Mint(string type, string subject, Dictionary<string, object?> payload, TenantId? tenant, DateTimeOffset now)
    {
        var provenance = new AlvoProvenance { CorrelationId = Guid.NewGuid().ToString("N"), Depth = 0 };
        using (AlvoFlow.Enter(provenance))
        {
            return AlvoFrameworkEvent.Create(type, subject, payload, AlvoContext.System(tenant) with { Provenance = provenance }, now);
        }
    }

    private static Dictionary<string, object?> Payload(string id, DateTimeOffset scheduledFor, bool manual)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ScheduleKey] = id,
            [JobInvocation.ScheduledForKey] = scheduledFor.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };
        if (manual)
        {
            payload[ManualKey] = true;
        }

        return payload;
    }

    private static InvalidOperationException NoTenantCatalog() => new(
        "A per-tenant schedule or data migration ran with no tenant list: the host registers no IAlvoTenantCatalog, and the "
        + "descriptor it was judged by declares no tenancy.selfService over a tenant registry. Register a catalog, declare "
        + "selfService, or use AlvoScheduleScope.Global.");
}
