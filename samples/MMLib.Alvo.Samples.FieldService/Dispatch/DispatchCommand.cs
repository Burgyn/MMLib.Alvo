using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Dispatch;

/// <summary><c>work_orders.dispatch</c>: assign a technician and a slot, and book the visit, as one unit.</summary>
/// <remarks>
/// <para>
/// <b>Why C#:</b> two writes to two entities must commit together or not at all — the job moves to <c>scheduled</c> with
/// its assignee and time, and the calendar gains the visit. The Data API writes one row per request (a batch is one
/// entity), so a client doing it in two calls could leave a job scheduled with no visit, or a visit on a job nobody
/// scheduled, when the second call is refused. A command makes it one transaction: the descriptor still judges every
/// write inside it — the state machine's <c>schedule</c> transition, the technician's exclusion on the calendar, the
/// service-hours hook — and any refusal rolls the whole unit back.
/// </para>
/// <para>
/// The access rule reads the row (<c>status == 'new'</c>) and the caller's role, and is judged after the row is read as
/// the caller — a job they cannot see is 404 — and before anything runs. An <c>Idempotency-Key</c> replays the first
/// answer, so a dispatcher's retried click books nothing twice.
/// </para>
/// </remarks>
internal static class DispatchCommand
{
    public const string Name = "work_orders.dispatch";

    public const string Route = "/commands/work-orders/{id:guid}/dispatch";

    public static WebApplication MapDispatch(this WebApplication app)
    {
        app.MapAlvoCommand<WorkOrder>(
            Name,
            Route,
            (order, caller) => order.Status == WorkOrderStatus.New
                && (caller.HasRole(FieldServiceRoles.Dispatcher) || caller.HasRole(FieldServiceRoles.Admin)),
            DispatchAsync);

        return app;
    }

    private static async Task<IResult> DispatchAsync(Guid id, DispatchRequest request, AlvoCommandContext ctx, CancellationToken ct)
    {
        var order = ctx.Record!.As<WorkOrder>();
        var visit = await ctx.TransactAsync(
            async (unit, token) =>
            {
                await unit.UpdateAsync(id, Scheduling(request), cancellationToken: token);
                var booked = await unit.CreateAsync(Booking(order, request), token);
                return booked!.Id;
            },
            cancellationToken: ct);

        return TypedResults.Ok(new DispatchResult(id, visit, order.JobNumber));
    }

    private static WorkOrderPatch Scheduling(DispatchRequest request) => WorkOrder.Patch()
        .SetAssignedTo(request.TechnicianId)
        .SetScheduledFor(request.StartsAt)
        .SetStatus(WorkOrderStatus.Scheduled);

    private static Visit Booking(WorkOrder order, DispatchRequest request) => Visit.Create(
        order.Id,
        request.TechnicianId,
        request.StartsAt,
        request.StartsAt.AddMinutes(request.DurationMinutes),
        tenantId: order.TenantId);
}
