using MMLib.Alvo.Admin.Internal;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// <c>j</c> and <c>k</c> on the data grid: one row at a time, from an end, and never off the page.
/// </summary>
public partial class RowCursorTests
{
    [Theory]
    [InlineData(null, "next", 0)]
    [InlineData(null, "previous", 2)]
    [InlineData(0, "next", 1)]
    [InlineData(1, "previous", 0)]
    public void A_press_moves_one_row_and_the_first_starts_at_the_end_it_points_to(
        int? selected, string direction, int expected)
        => RowCursor.Move(selected, direction, 3).ShouldBe(expected);

    [Theory]
    [InlineData(2, "next", 2)]
    [InlineData(0, "previous", 0)]
    public void The_selection_stops_at_the_ends_rather_than_wrapping(int selected, string direction, int expected)
        => RowCursor.Move(selected, direction, 3).ShouldBe(expected);

    [Fact]
    public void A_selection_past_a_shorter_page_starts_again_from_its_end()
    {
        RowCursor.Move(5, "next", 3).ShouldBe(0);
        RowCursor.Move(5, "previous", 3).ShouldBe(2);
    }

    [Fact]
    public void An_empty_page_selects_nothing() => RowCursor.Move(null, "next", 0).ShouldBeNull();

    /// <summary>
    /// alvo.js names the direction without asking .NET; a word the two spell differently would make
    /// <c>k</c> move down.
    /// </summary>
    [Fact]
    public void The_direction_alvo_js_sends_for_k_is_the_one_read_here()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "wwwroot", "alvo.js"));

        MoveOnK().Match(script).Groups[1].Value.ShouldBe(RowCursor.Previous);
    }

    [GeneratedRegex(@"case 'k':\s+event\.preventDefault\(\);\s+emit\('move', \{ value: '([a-z]+)' \}\);")]
    private static partial Regex MoveOnK();
}
