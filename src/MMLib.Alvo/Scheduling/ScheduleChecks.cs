using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

using Kinds = MMLib.Alvo.Events.Internal.DeliveryKind;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The boot's checks of every schedule, all reported at once: a cron Cronos cannot read or that never fires, a zone this
/// machine does not know, and a per-tenant schedule with no tenant list — and, against the catalog the boot primes, a data
/// migration not yet started on a scoped schema with no tenant list.
/// </summary>
/// <remarks>
/// <b>A tenant list is <see cref="TenantCatalogs.Lists(IServiceProviderIsService, bool)"/>'s answer for the descriptor
/// judged</b> (S3 plan D13): the host's <see cref="IAlvoTenantCatalog"/>, or the self-service registry under a descriptor that
/// declares <c>tenancy.selfService</c>. The catalog-free pass judges as if the descriptor could declare it, so it refuses only
/// what no descriptor can cure — no catalog and no registry; each catalog's own pass refuses the rest, the boot's strictly.
/// </remarks>
internal static class ScheduleChecks
{
    internal const string Kind = "schedule";

    /// <summary>Every schedule's problems under a descriptor that does or does not declare <c>tenancy.selfService</c>.</summary>
    /// <param name="registry">The registered extensions.</param>
    /// <param name="services">Answers which services the container can resolve.</param>
    /// <param name="now">The instant a cron must still fire after.</param>
    /// <param name="selfService">
    /// Whether the descriptor judged declares <c>tenancy.selfService</c>; <see langword="true"/> on the catalog-free pass, which
    /// refuses only what no descriptor can cure.
    /// </param>
    internal static IEnumerable<ExtensionProblem> Check(ExtensionRegistry registry, IServiceProviderIsService services, DateTimeOffset now, bool selfService)
    {
        var lists = new TenantListing(TenantCatalogs.Lists(services, selfService), TenantCatalogs.Registry(services));
        return registry.Handlers.Where(handler => handler.Job is { IsSchedule: true }).SelectMany(handler => Problems(handler, handler.Job!, lists, now));
    }

    /// <summary>
    /// Why nothing lists the tenants (S3 plan D13): on a host with a self-service registry it is the descriptor that lacks
    /// <c>tenancy.selfService</c> as much as the host that lacks a catalog, so the text names both.
    /// </summary>
    /// <param name="registry">Whether the host registers a self-service tenant registry.</param>
    internal static string NoTenantList(bool registry) => registry
        ? "nothing lists them under this descriptor: this host registers no IAlvoTenantCatalog, and the descriptor does not "
            + "declare tenancy.selfService, whose registry would"
        : "this host registers no IAlvoTenantCatalog to list them";

    /// <summary>What cures <see cref="NoTenantList(bool)"/>, the most direct cure first.</summary>
    /// <param name="registry">Whether the host registers a self-service tenant registry.</param>
    internal static string ListTheTenants(bool registry) => registry
        ? "Declare tenancy.selfService in the descriptor, or register an IAlvoTenantCatalog in DI"
        : "Register an IAlvoTenantCatalog in DI";

    /// <summary>
    /// The boot's own check of every data migration not yet started against the schema it primes: a scoped schema needs a tenant
    /// list. Not part of the per-catalog binding, so an apply that first scopes the schema is not refused — its migration waits
    /// at run time; and a migration that has run needs nothing more.
    /// </summary>
    internal static IEnumerable<ExtensionProblem> CheckBoot(
        ExtensionRegistry registry, PolicyCatalog catalog, IServiceProviderIsService services, IReadOnlySet<string> started)
    {
        if (!NeedsTenants(registry, catalog, services))
        {
            return [];
        }

        var tenantRegistry = TenantCatalogs.Registry(services);
        return registry.Handlers.Where(handler => handler.Job is { IsMigration: true } && !started.Contains(handler.Id)).Select(handler => new ExtensionProblem(
            handler.Id,
            Kinds.Migration,
            $"extensions/{handler.Id}/tenants",
            $"'{handler.Id}' has not run yet and runs once per tenant on this scoped schema, and {NoTenantList(tenantRegistry)}.",
            $"{ListTheTenants(tenantRegistry)}."));
    }

    /// <summary>Whether any registered data migration could need a tenant list this host lacks on <paramref name="catalog"/>.</summary>
    internal static bool NeedsTenants(ExtensionRegistry registry, PolicyCatalog catalog, IServiceProviderIsService services) =>
        registry.Handlers.Any(handler => handler.Job is { IsMigration: true })
        && IsScoped(catalog.Schema)
        && !TenantCatalogs.Lists(services, catalog.SelfService.Enabled);

    /// <summary>Whether <paramref name="schema"/> has an entity whose rows belong to a tenant.</summary>
    internal static bool IsScoped(SchemaModel schema) => schema.Entities.Any(entity => entity.Tenancy == TenancyMode.Scoped);

    private static IEnumerable<ExtensionProblem> Problems(HandlerRegistration handler, JobSpec job, TenantListing tenants, DateTimeOffset now)
    {
        if (CronSchedule.CronProblem(job.Cron!, now) is { } cron)
        {
            yield return Problem(handler.Id, "cron", cron, "Write five fields — minute hour day-of-month month day-of-week — such as '0 2 * * *'.");
        }

        if (CronSchedule.ZoneProblem(job.TimeZone!) is { } zone)
        {
            yield return Problem(handler.Id, "timeZone", zone, "Use an IANA time zone id, such as 'Europe/Bratislava', or leave the default 'UTC'.");
        }

        if (job.Scope is AlvoScheduleScope.EachTenant && !tenants.Lists)
        {
            yield return Problem(
                handler.Id,
                "scope",
                $"'{handler.Id}' runs once per tenant, and {NoTenantList(tenants.Registry)}.",
                $"{ListTheTenants(tenants.Registry)}, or use AlvoScheduleScope.Global.");
        }
    }

    /// <summary>Whether a tenant list exists under the descriptor judged, and whether the host has a registry that could be one.</summary>
    private readonly record struct TenantListing(bool Lists, bool Registry);

    private static ExtensionProblem Problem(string id, string slot, string message, string fix) =>
        new(id, Kind, $"extensions/{id}/{slot}", message, fix);
}
