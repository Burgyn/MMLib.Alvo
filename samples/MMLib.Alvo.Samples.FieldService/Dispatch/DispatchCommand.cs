using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Dispatch;

/// <summary><c>work_orders.dispatch</c>: assign a technician and a slot, and book the visit, as one unit.</summary>
/// <remarks>
/// <para>
/// <b>Why C#:</b> two writes to two entities must commit together or not at all — the job moves to <c>scheduled</c> with
/// its assignee and time, and the calendar gains the visit. The descriptor has no multi-entity transaction (a Data API
/// request or batch writes one entity), so a client doing it in two calls could leave a job scheduled with no visit, or a
/// visit on a job nobody scheduled, when the second call is refused. A command makes it one transaction: the descriptor
/// still judges every write inside it — the state machine's <c>schedule</c> transition, the technician's exclusion on the
/// calendar, the service-hours hooks — and any refusal rolls the whole unit back.
/// </para>
/// <para>
/// The access rule is the caller's role only, the same roles as the descriptor's <c>schedule</c> transition; it is judged
/// after the row is read as the caller, so a job in another tenant is 404. A job that is not <c>new</c> any more is a
/// state conflict, 409, not a refusal of the caller. The input is checked before anything is written, so a missing field
/// or an absurd duration is a 422 naming it. An <c>Idempotency-Key</c> replays the first answer.
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
            (order, caller) => caller.HasRole(FieldServiceRoles.Dispatcher) || caller.HasRole(FieldServiceRoles.Admin),
            DispatchAsync);

        return app;
    }

    private static async Task<IResult> DispatchAsync(Guid id, DispatchRequest request, AlvoCommandContext ctx, CancellationToken ct)
    {
        if (request.Violations() is { Count: > 0 } violations)
        {
            return FieldServiceProblems.Invalid(violations);
        }

        var order = ctx.Record!.As<WorkOrder>();
        if (order.Status != WorkOrderStatus.New)
        {
            return FieldServiceProblems.Conflict($"Only a new job is dispatched; {order.Reference} is already past that.");
        }

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
        .SetAssignedTo(request.TechnicianId!.Value)
        .SetScheduledFor(request.StartsAt!.Value)
        .SetStatus(WorkOrderStatus.Scheduled);

    private static Visit Booking(WorkOrder order, DispatchRequest request) => Visit.Create(
        order.Id,
        request.TechnicianId!.Value,
        request.StartsAt!.Value,
        request.StartsAt.Value.AddMinutes(request.DurationMinutes!.Value),
        tenantId: order.TenantId);
}
