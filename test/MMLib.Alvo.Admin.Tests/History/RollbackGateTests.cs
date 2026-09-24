using MMLib.Alvo.Admin.Components.History;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.History;

/// <summary>
/// A rollback runs only after its plan is on screen, and permission to lose data is its own explicit confirm —
/// never implied by having typed a name (docs/todo-admin.md §8b, rollback dry run).
/// </summary>
public class RollbackGateTests
{
    private static readonly ManagementPlanSummary _safe = new(IsEmpty: false, HasDestructiveChanges: false, ["AddField a.b"]);
    private static readonly ManagementPlanSummary _destroys = new(IsEmpty: false, HasDestructiveChanges: true, ["DropField a.b  <- destructive"]);

    [Fact]
    public void Nothing_rolls_back_before_its_plan_is_on_screen()
        => RollbackGate.CanRollBack(null, confirmed: true).ShouldBeFalse();

    [Fact]
    public void A_plan_that_destroys_nothing_rolls_back_without_a_confirmation()
        => RollbackGate.CanRollBack(_safe, confirmed: false).ShouldBeTrue();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_plan_that_destroys_data_waits_for_its_confirmation(bool confirmed, bool can)
        => RollbackGate.CanRollBack(_destroys, confirmed).ShouldBe(can);

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Permission_to_destroy_is_sent_only_for_a_confirmed_destructive_plan(bool destructive, bool confirmed, bool sent)
        => RollbackGate.AllowDestructive(destructive ? _destroys : _safe, confirmed).ShouldBe(sent);
}
