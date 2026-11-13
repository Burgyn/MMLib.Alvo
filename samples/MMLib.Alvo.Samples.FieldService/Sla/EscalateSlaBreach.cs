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
/// It runs as the system in the job's tenant and re-reads the row first: a job scheduled meanwhile, no longer an
/// emergency, or given a later deadline is left alone — the write that changed it cancelled or moved the timer, and this
/// check covers a delivery already in flight. The page goes first, as a named step: if raising the priority then fails,
/// the retried delivery skips the page that already went out and only raises the priority.
/// </para>
/// </remarks>
internal sealed class EscalateSlaBreach(IFieldServiceNotifier notifier) : IAlvoEventHandler<SlaBreached>
{
    public const string Id = "escalate-sla-breach";

    private const long TopPriority = 1;

    public async Task HandleAsync(SlaBreached breach, AlvoEventContext context, CancellationToken cancellationToken)
    {
        var job = await context.Data.GetAsync<WorkOrder>(breach.Id, cancellationToken);
        if (job is null || !StillBreached(job, breach))
        {
            return;
        }

        await context.Steps.RunAsync(
            "page-on-call",
            token => notifier.PageOnCallAsync(new SlaBreachNotice(job.Id, job.Reference, job.JobNumber, breach.At), token),
            cancellationToken);
        await context.Data.UpdateAsync(job.Id, WorkOrder.Patch().SetPriority(TopPriority), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The row as it is now still breaches the deadline that fired: an unscheduled emergency whose <c>respond_by</c> has not
    /// moved past it. A deadline moved later re-armed the timer for the new time, and that timer's own event will come.
    /// </summary>
    private static bool StillBreached(WorkOrder job, SlaBreached breach) =>
        job.Status == WorkOrderStatus.New
        && job.IsEmergency == true
        && job.RespondBy is { } due
        && due <= breach.At;
}
