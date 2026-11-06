using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The boot's checks of every schedule, all reported at once: a cron Cronos cannot read or that never fires, a zone this
/// machine does not know, and a per-tenant schedule in a host with no <see cref="IAlvoTenantCatalog"/> to list the tenants.
/// </summary>
internal static class ScheduleChecks
{
    internal const string Kind = "schedule";

    internal static IEnumerable<ExtensionProblem> Check(ExtensionRegistry registry, IServiceProviderIsService services, DateTimeOffset now) =>
        registry.Handlers.Where(handler => handler.Job is { IsSchedule: true }).SelectMany(handler => Problems(handler, handler.Job!, services, now));

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
