using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Mobile;

/// <summary>The technician app's signed "job completed" webhook: completes the job and records the minutes on site.</summary>
/// <remarks>
/// <para>
/// <b>Why C#:</b> the descriptor describes what Alvo sends out (<c>webhooks</c>, <c>automation</c>), not what it receives.
/// An inbound Standard Webhook needs a route, a signature check over the exact bytes, deduplication by
/// <c>webhook-id</c>, and a tenant taken from the signed body — a receiver does all four before this code runs. The writes
/// then take two legal moves of the descriptor's state machine (<c>start</c> if the job was only scheduled, then
/// <c>complete</c>) and the visit's minutes, in one unit; the descriptor's rollup carries the minutes to the job.
/// </para>
/// <para>
/// The handler runs as the system, which holds <c>admin</c> — every transition this needs lists it. The tenant comes from
/// the signed body only: the path, the query and every other header are not signed. A notice that names no tenant, or the
/// all-zero one, resolves to none, which every tenant-scoped entity refuses; a missing field is a 422 naming it. A notice for a job already completed
/// is acknowledged and changes nothing, so the app may resend under a new id; one for a job this tenant does not hold is
/// 404, and one for a job nobody scheduled is 409 — both given back, so the app's retry runs again.
/// </para>
/// <para>
/// <b>Trust:</b> one <c>technician_app</c> secret signs for every tenant, so whoever holds it may complete a job in any
/// tenant by naming it — platform-wide trust in the app's backend. A notice naming tenant A still cannot touch tenant B's
/// job (it is read under tenant A, and answers 404). Where each tenant's app signs for itself, give each tenant its own
/// secret and receiver (<c>options.Tenant</c> per route, or a group per tenant): the stronger option.
/// </para>
/// </remarks>
internal static class JobCompletedReceiver
{
    public const string Route = "/hooks/technician-app";

    public const string SecretName = "technician_app";

    private enum Outcome
    {
        Completed,
        AlreadyCompleted,
        NotFound,
        NotStarted,
    }

    public static WebApplication MapTechnicianApp(this WebApplication app)
    {
        app.MapAlvoWebhookReceiver(Route, SecretName, CompleteAsync, options => options.ResolveTenant = TenantOf);
        return app;
    }

    private static ValueTask<TenantId?> TenantOf(HttpRequest request, ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
        ValueTask.FromResult(JobCompleted.TenantOf(body));

    private static async Task<IResult> CompleteAsync(
        JobCompleted notice, AlvoCaller caller, IAlvoTransactions transactions, CancellationToken ct)
    {
        if (notice.Violations() is { Count: > 0 } violations)
        {
            return FieldServiceProblems.Invalid(violations);
        }

        var outcome = await transactions.TransactAsync(
            caller.Context, (unit, token) => CompleteInUnitAsync(unit, notice, token), cancellationToken: ct);

        return outcome switch
        {
            Outcome.NotFound => TypedResults.NotFound(),
            Outcome.NotStarted => TypedResults.Conflict(),
            _ => TypedResults.Ok(),
        };
    }

    private static async Task<Outcome> CompleteInUnitAsync(IAlvoTransaction unit, JobCompleted notice, CancellationToken ct)
    {
        var order = await unit.GetAsync<WorkOrder>(notice.WorkOrderId!.Value, ct);
        if (order is null)
        {
            return Outcome.NotFound;
        }

        if (order.Status == WorkOrderStatus.Completed)
        {
            return Outcome.AlreadyCompleted;
        }

        if (order.Status is not (WorkOrderStatus.Scheduled or WorkOrderStatus.InProgress))
        {
            return Outcome.NotStarted;
        }

        await RecordMinutesAsync(unit, order.Id, notice.MinutesOnSite!.Value, ct);
        await MoveToCompletedAsync(unit, order, notice.CompletedOn!.Value, ct);
        return Outcome.Completed;
    }

    /// <summary>
    /// Records the minutes on the job's latest visit — BEFORE the job completes, because the descriptor's lookup check
    /// refuses any visit write on a completed job.
    /// </summary>
    private static async Task RecordMinutesAsync(IAlvoTransaction unit, Guid workOrderId, int minutes, CancellationToken ct)
    {
        var visits = await unit.QueryAsync<Visit>(x => x.WorkOrderId == workOrderId, limit: 50, ct);
        if (visits.MaxBy(visit => visit.StartsAt) is { } latest)
        {
            await unit.UpdateAsync(latest.Id, Visit.Patch().SetMinutesOnSite(minutes), cancellationToken: ct);
        }
    }

    private static async Task MoveToCompletedAsync(IAlvoTransaction unit, WorkOrder order, DateOnly completedOn, CancellationToken ct)
    {
        if (order.Status == WorkOrderStatus.Scheduled)
        {
            await unit.UpdateAsync(order.Id, WorkOrder.Patch().SetStatus(WorkOrderStatus.InProgress), cancellationToken: ct);
        }

        await unit.UpdateAsync(
            order.Id,
            WorkOrder.Patch().SetStatus(WorkOrderStatus.Completed).SetCompletedOn(completedOn),
            cancellationToken: ct);
    }
}
