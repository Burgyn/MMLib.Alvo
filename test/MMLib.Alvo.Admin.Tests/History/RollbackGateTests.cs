using MMLib.Alvo.Admin.Components.History;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.History;

/// <summary>
/// Permission to lose data is sent only for a plan that destroys data and a confirmation the operator gave
/// (docs/todo-admin.md §8b, rollback dry run).
/// </summary>
public class RollbackGateTests
{
    private static readonly ManagementPlanSummary _safe = new(IsEmpty: false, HasDestructiveChanges: false, ["AddField a.b"]);
    private static readonly ManagementPlanSummary _destroys = new(IsEmpty: false, HasDestructiveChanges: true, ["DropField a.b  <- destructive"]);

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Permission_to_destroy_is_sent_only_for_a_confirmed_destructive_plan(bool destructive, bool confirmed, bool sent)
        => RollbackGate.AllowDestructive(destructive ? _destroys : _safe, confirmed).ShouldBe(sent);
}
