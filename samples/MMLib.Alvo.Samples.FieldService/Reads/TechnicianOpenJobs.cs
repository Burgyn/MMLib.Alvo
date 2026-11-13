using MMLib.Alvo.Reads;
using MMLib.Alvo.Samples.FieldService.Model;
using System.Linq.Expressions;

namespace MMLib.Alvo.Samples.FieldService.Reads;

/// <summary>A technician's job list shows only the jobs still to do: <c>new</c>, <c>scheduled</c> or <c>in_progress</c>.</summary>
/// <remarks>
/// <para>
/// <b>Why C#:</b> this is a <em>view</em> for the technician app, not a permission. Writing it into the descriptor's
/// <c>work_orders.list</c> rule would make it one: a technician could then never read their own finished job, and the
/// rule would no longer say who may see what but what one app wants to show. A narrowing is ANDed after the rules, never
/// widens them, and applies to the technician's list, get and count; the dispatcher and the administrator are not
/// narrowed, nor is the system.
/// </para>
/// <para>
/// It is not a boundary: a write still reaches a row it hides. Who may touch a job stays in the descriptor's rules.
/// </para>
/// </remarks>
internal sealed class TechnicianOpenJobs : IQueryNarrowing<WorkOrder>
{
    public const string Id = "technician-open-jobs";

    public Expression<Func<WorkOrder, bool>>? Narrow(QueryNarrowing query) =>
        IsDesk(query.Caller)
            ? null
            : x => x.Status == WorkOrderStatus.New || x.Status == WorkOrderStatus.Scheduled || x.Status == WorkOrderStatus.InProgress;

    private static bool IsDesk(AlvoContext caller) =>
        caller.Roles.Any(role => role.Name is FieldServiceRoles.Dispatcher or FieldServiceRoles.Admin);
}
