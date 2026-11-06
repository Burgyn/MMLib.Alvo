using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;

using System.Globalization;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Mints the framework events of one schedule occurrence: one per tenant the <see cref="IAlvoTenantCatalog"/> lists for an
/// <see cref="AlvoScheduleScope.EachTenant"/> schedule, else one with no tenant — each as the system in its tenant, under a
/// correlation of its own at depth 0, whatever flow asked for it.
/// </summary>
/// <param name="tenants">The host's tenant list, or <see langword="null"/> when it registers none; the boot refuses a per-tenant schedule without one.</param>
/// <param name="time">The clock each event is stamped with.</param>
internal sealed class ScheduleRuns(IAlvoTenantCatalog? tenants, TimeProvider time)
{
    internal const string ScheduleKey = "schedule";
    internal const string ManualKey = "manual";

    /// <summary>The events of <paramref name="job"/>'s run for <paramref name="scheduledFor"/>, in the order the tenants are listed.</summary>
    /// <param name="job">A registered schedule.</param>
    /// <param name="scheduledFor">The occurrence; for a run started on demand, the moment it was started.</param>
    /// <param name="manual">Whether an operator started the run, rather than the cron.</param>
    /// <param name="ct">A token to cancel the tenant listing.</param>
    internal async Task<IReadOnlyList<AlvoFrameworkEnvelope>> EventsAsync(
        HandlerRegistration job, DateTimeOffset scheduledFor, bool manual, CancellationToken ct)
    {
        var type = ((NamedEvent)job.Source).Type;
        var payload = Payload(job.Id, scheduledFor, manual);
        var now = time.GetUtcNow();
        var scopes = await ScopesAsync(job.Job!, ct).ConfigureAwait(false);

        return [.. scopes.Select(tenant => Mint(type, $"schedules/{job.Id}", payload, tenant, now))];
    }

    private async Task<IReadOnlyList<TenantId?>> ScopesAsync(JobSpec job, CancellationToken ct)
    {
        if (job.Scope is not AlvoScheduleScope.EachTenant)
        {
            return [null];
        }

        var listed = await (tenants ?? throw NoTenantCatalog()).ListTenantsAsync(ct).ConfigureAwait(false);
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
        "A per-tenant schedule ran in a host that registers no IAlvoTenantCatalog. Register one, or use AlvoScheduleScope.Global.");
}
