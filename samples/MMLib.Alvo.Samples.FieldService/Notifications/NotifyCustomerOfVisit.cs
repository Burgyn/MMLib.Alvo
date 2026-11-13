using MMLib.Alvo.Events;
using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Notifications;

/// <summary>When a job becomes <c>scheduled</c>, tells the customer when the technician is coming.</summary>
/// <remarks>
/// <b>Why C#:</b> the message goes to an SMS or e-mail gateway with its own client, not to a webhook endpoint the
/// descriptor could declare — and a descriptor <c>email</c> action would need a configured mail provider the demo does not
/// run. A typed handler runs after the commit, at least once, retried on failure; the <c>when</c> lambda is translated to
/// CEL at registration (<c>changed(status) &amp;&amp; new.status == 'scheduled'</c>), so the catalog shows it.
/// </remarks>
internal sealed class NotifyCustomerOfVisit(IFieldServiceNotifier notifier) : IAlvoEventHandler<AlvoUpdated<WorkOrder>>
{
    public const string Id = "notify-customer-visit";

    public Task HandleAsync(AlvoUpdated<WorkOrder> scheduled, AlvoEventContext context, CancellationToken cancellationToken)
    {
        var job = scheduled.New;
        return notifier.NotifyCustomerAsync(
            new VisitNotice(job.Id, job.Reference, job.JobNumber, job.ContactEmail, job.ScheduledFor), cancellationToken);
    }
}
