using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.History;

/// <summary>
/// Whether a rollback may destroy data — the Preview's rule, applied to a rollback.
/// </summary>
/// <remarks>
/// <b>It used to run blind and always destructive</b>: no plan, and <c>allowDestructive: true</c> hard-coded after a
/// typed-name confirm, beside a sentence saying "permission to lose data is never implied" (docs/todo-admin.md §8b).
/// Now the plan is asked first as a dry run (which asks with destruction allowed, because describing a drop destroys
/// nothing — Preview's reason), and the real rollback sends the permission only when the plan needs it and the
/// operator gave it. When it may run is the page's structure now: Roll back is drawn only under a plan, and every
/// rollback asks for the project's name (spec §3.2).
/// </remarks>
internal static class RollbackGate
{
    /// <summary>What the real rollback sends as <c>allowDestructive</c>.</summary>
    /// <param name="plan">The plan on screen.</param>
    /// <param name="confirmed">Whether the operator typed the confirmation.</param>
    public static bool AllowDestructive(ManagementPlanSummary plan, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.HasDestructiveChanges && confirmed;
    }
}
