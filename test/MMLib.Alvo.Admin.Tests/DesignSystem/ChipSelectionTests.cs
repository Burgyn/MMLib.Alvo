using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>
/// What a press and a key do to a group of chips.
/// </summary>
public class ChipSelectionTests
{
    [Fact]
    public void Pressing_an_unchosen_chip_appends_it_after_the_ones_already_chosen()
        => ChipSelection.Toggle(["dispatcher", "technician"], "admin")
            .ShouldBe(["dispatcher", "technician", "admin"]);

    [Fact]
    public void Pressing_a_chosen_chip_removes_it_and_keeps_the_others_in_their_order()
        => ChipSelection.Toggle(["dispatcher", "technician", "admin"], "technician")
            .ShouldBe(["dispatcher", "admin"]);

    /// <summary>A role is a name, and <c>Admin</c> is not <c>admin</c>.</summary>
    [Fact]
    public void Strings_are_compared_ordinally()
        => ChipSelection.Toggle(["admin"], "Admin").ShouldBe(["admin", "Admin"]);

    [Fact]
    public void The_selection_it_was_given_is_not_changed()
    {
        string[] selected = ["dispatcher"];

        ChipSelection.Toggle(selected, "dispatcher");

        selected.ShouldBe(["dispatcher"]);
    }

    [Theory]
    [InlineData(0, "ArrowRight", 1)]
    [InlineData(0, "ArrowDown", 1)]
    [InlineData(3, "ArrowRight", 0)]
    [InlineData(0, "ArrowLeft", 3)]
    [InlineData(2, "ArrowUp", 1)]
    [InlineData(2, "Home", 0)]
    [InlineData(1, "End", 3)]
    public void The_arrows_wrap_in_both_axes_and_home_and_end_reach_the_ends(int from, string key, int to)
        => ChipSelection.Move(4, from, key).ShouldBe(to);

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    [InlineData("Tab")]
    public void Any_other_key_moves_nothing(string key)
        => ChipSelection.Move(4, 1, key).ShouldBeNull();

    [Fact]
    public void An_empty_group_has_nowhere_to_move()
        => ChipSelection.Move(0, 0, "ArrowRight").ShouldBeNull();
}
