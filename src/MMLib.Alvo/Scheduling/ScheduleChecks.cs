using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

using Kinds = MMLib.Alvo.Events.Internal.DeliveryKind;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The boot's checks of every schedule, all reported at once: a cron Cronos cannot read or that never fires, a zone this
/// machine does not know, and a per-tenant schedule in a host with no <see cref="IAlvoTenantCatalog"/> to list the tenants — and,
/// against the catalog the boot primes, a data migration not yet started on a scoped schema with no tenant list.
/// </summary>
internal static class ScheduleChecks
{
    internal const string Kind = "schedule";

    internal static IEnumerable<ExtensionProblem> Check(ExtensionRegistry registry, IServiceProviderIsService services, DateTimeOffset now) =>
        registry.Handlers.Where(handler => handler.Job is { IsSchedule: true }).SelectMany(handler => Problems(handler, handler.Job!, services, now));

    /// <summary>
    /// The boot's own check of every data migration not yet started against the schema it primes: a scoped schema needs a tenant
    /// list. Not part of the per-catalog binding, so an apply that first scopes the schema is not refused — its migration waits
    /// at run time; and a migration that has run needs nothing more.
    /// </summary>
    internal static IEnumerable<ExtensionProblem> CheckBoot(
        ExtensionRegistry registry, PolicyCatalog catalog, IServiceProviderIsService services, IReadOnlySet<string> started) =>
        NeedsTenants(registry, catalog, services)
            ? registry.Handlers.Where(handler => handler.Job is { IsMigration: true } && !started.Contains(handler.Id)).Select(handler => new ExtensionProblem(
                handler.Id,
                Kinds.Migration,
                $"extensions/{handler.Id}/tenants",
                $"'{handler.Id}' has not run yet and runs once per tenant on this scoped schema, and this host registers no "
                    + "IAlvoTenantCatalog to list them.",
                "Register an IAlvoTenantCatalog in DI."))
            : [];

    /// <summary>Whether any registered data migration could need a tenant list this host lacks on <paramref name="catalog"/>.</summary>
    internal static bool NeedsTenants(ExtensionRegistry registry, PolicyCatalog catalog, IServiceProviderIsService services) =>
        registry.Handlers.Any(handler => handler.Job is { IsMigration: true })
        && IsScoped(catalog.Schema)
        && !services.IsService(typeof(IAlvoTenantCatalog));

    /// <summary>Whether <paramref name="schema"/> has an entity whose rows belong to a tenant.</summary>
    internal static bool IsScoped(SchemaModel schema) => schema.Entities.Any(entity => entity.Tenancy == TenancyMode.Scoped);

    private static IEnumerable<ExtensionProblem> Problems(HandlerRegistration handler, JobSpec job, IServiceProviderIsService services, DateTimeOffset now)
    {
        if (CronSchedule.CronProblem(job.Cron!, now) is { } cron)
        {
            yield return Problem(handler.Id, "cron", cron, "Write five fields — minute hour day-of-month month day-of-week — such as '0 2 * * *'.");
        }

        if (CronSchedule.ZoneProblem(job.TimeZone!) is { } zone)
        {
            yield return Problem(handler.Id, "timeZone", zone, "Use an IANA time zone id, such as 'Europe/Bratislava', or leave the default 'UTC'.");
        }

        if (job.Scope is AlvoScheduleScope.EachTenant && !services.IsService(typeof(IAlvoTenantCatalog)))
        {
            yield return Problem(
                handler.Id,
                "scope",
                $"'{handler.Id}' runs once per tenant, and this host registers no IAlvoTenantCatalog to list them.",
                "Register an IAlvoTenantCatalog in DI, or use AlvoScheduleScope.Global.");
        }
    }

    private static ExtensionProblem Problem(string id, string slot, string message, string fix) =>
        new(id, Kind, $"extensions/{id}/{slot}", message, fix);
}
