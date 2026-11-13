using MMLib.Alvo.Data;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.Samples.FieldService.Notifications;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Samples.FieldService.Reports;

/// <summary>Every morning, per tenant, the open jobs past their slot or their respond-by time go to the dispatch desk.</summary>
/// <remarks>
/// <para>
/// <b>Why C#:</b> a descriptor cron automation takes no condition and runs once per tenant only with an
/// <see cref="IAlvoTenantCatalog"/>, which the standalone image does not register; and the report is a query plus a message
/// to a gateway, not a write or a webhook. A scheduled job runs one occurrence once across every replica, per tenant
/// (<see cref="AlvoScheduleScope.EachTenant"/>), as the system in that tenant — so each run reads only its own rows.
/// </para>
/// <para>
/// "Overdue" is judged against the occurrence (<see cref="AlvoJobContext.ScheduledFor"/>), not the wall clock, so a run
/// retried later reports the same morning. The cron comes from configuration (<c>FieldService:OverdueReportCron</c>), and
/// an operator starts a run outside it with the Management API's <em>Run now</em> — the end-to-end suite does exactly that,
/// so there is no test switch in this host.
/// </para>
/// </remarks>
internal sealed class OverdueReportJob(IFieldServiceNotifier notifier) : IAlvoScheduledJob
{
    public const string Id = "overdue-report";

    public const string Description = "Every morning, per tenant, the open jobs past their slot or their respond-by time.";

    private const int MaxOpenJobs = 500;

    public async Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
    {
        if (context.Tenant is not { } tenant)
        {
            return;
        }

        var open = await context.Data.QueryAsync<WorkOrder>(
            x => x.Status == WorkOrderStatus.New || x.Status == WorkOrderStatus.Scheduled, MaxOpenJobs, cancellationToken);
        var overdue = open
            .Where(job => IsOverdue(job, context.ScheduledFor))
            .Select(job => job.Reference)
            .Order(StringComparer.Ordinal)
            .ToList();

        await notifier.SendOverdueReportAsync(new OverdueReport(tenant.Value, context.ScheduledFor, overdue), cancellationToken);
    }

    private static bool IsOverdue(WorkOrder job, DateTimeOffset at) =>
        job.Status == WorkOrderStatus.Scheduled ? job.ScheduledFor < at : job.RespondBy < at;
}
