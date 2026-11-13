using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.Samples.FieldService.Notifications;

namespace MMLib.Alvo.Samples.FieldService.Sla;

/// <summary>The payload of the descriptor's <c>sla_breach</c> timer when it fires.</summary>
/// <param name="Entity">The entity the timer follows: <c>work_orders</c>.</param>
/// <param name="Id">The job whose deadline passed.</param>
/// <param name="Timer">The timer's name: <c>sla_breach</c>.</param>
/// <param name="At">The deadline — the job's <c>respond_by</c> when the timer was armed.</param>
[AlvoEvent(EventType)]
internal sealed record SlaBreached(string Entity, Guid Id, string Timer, DateTimeOffset At)
{
    public const string EventType = "work_orders.sla_breached";
}

/// <summary>An emergency still unscheduled at its respond-by time goes to priority 1, and the on-call dispatcher is paged.</summary>
/// <remarks>
/// <para>
/// <b>Why C#:</b> the descriptor arms, moves and cancels the timer in the write's own transaction, but descriptor
/// automation cannot subscribe to a timer's event (state-machines.mdx, <em>What is not covered</em>) — so in the standalone
/// image the breach is published and nobody acts on it. A host handler closes that loop.
/// </para>
/// <para>
/// It runs as the system in the job's tenant. The page is a named step, so a retry after the page failed raises the
/// priority again (harmless) but never pages twice once the page went out. A job scheduled meanwhile is left alone: the
/// timer is cancelled in the write that scheduled it, and this check covers a delivery already in flight.
/// </para>
/// </remarks>
internal sealed class EscalateSlaBreach(IFieldServiceNotifier notifier) : IAlvoEventHandler<SlaBreached>
{
    public const string Id = "escalate-sla-breach";

    private const long TopPriority = 1;

    public async Task HandleAsync(SlaBreached breach, AlvoEventContext context, CancellationToken cancellationToken)
    {
        var job = await context.Data.GetAsync<WorkOrder>(breach.Id, cancellationToken);
        if (job is not { Status: WorkOrderStatus.New })
        {
            return;
        }

        await context.Data.UpdateAsync(job.Id, WorkOrder.Patch().SetPriority(TopPriority), cancellationToken: cancellationToken);
        await context.Steps.RunAsync(
            "page-on-call",
            token => notifier.PageOnCallAsync(new SlaBreachNotice(job.Id, job.Reference, job.JobNumber, breach.At), token),
            cancellationToken);
    }
}
