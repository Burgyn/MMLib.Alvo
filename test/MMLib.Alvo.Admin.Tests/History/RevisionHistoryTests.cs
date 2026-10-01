using MMLib.Alvo.Admin.Components.History;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.History;

/// <summary>
/// The configuration history in the order it is read, and the words a revision is shown with.
/// </summary>
/// <remarks>
/// Overview took the first entry of the contract's oldest-first list as "the latest change", so after an
/// apply it still showed revision 1 (D-2), and the history screen listed oldest first (D-3).
/// </remarks>
public class RevisionHistoryTests
{
    private static readonly DateTimeOffset _at = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_newest_revision_comes_first_whatever_order_the_contract_answered_in()
    {
        var ordered = RevisionHistory.NewestFirst([Revision(1), Revision(3), Revision(2)]);

        ordered.Select(revision => revision.Revision).ShouldBe([3, 2, 1]);
    }

    [Fact]
    public void Two_applies_in_one_clock_tick_are_ordered_by_their_number()
    {
        var ordered = RevisionHistory.NewestFirst([Revision(1), Revision(2)]);

        ordered[0].Revision.ShouldBe(2, "the number is what the log is ordered by; the timestamps tie");
    }

    [Fact]
    public void The_first_revision_with_no_reason_is_the_initial_descriptor()
    {
        var initial = Revision(1);

        RevisionHistory.Title(initial).ShouldBe("Initial descriptor");
        RevisionHistory.Author(initial).ShouldBeNull("the title already says what it is");
    }

    [Fact]
    public void A_later_revision_with_no_reason_says_so_and_who_applied_it()
    {
        var later = Revision(4);

        RevisionHistory.Title(later).ShouldBe("No reason recorded");
        RevisionHistory.Author(later).ShouldBe("code-first or system");
    }

    [Fact]
    public void A_recorded_reason_and_author_are_shown_as_recorded()
    {
        var first = Revision(1) with { Reason = "Seed the workshop", Author = "eva@example.com" };

        RevisionHistory.Title(first).ShouldBe("Seed the workshop");
        RevisionHistory.Author(first).ShouldBe("eva@example.com");
    }

    private static ManagementRevision Revision(int number)
        => new(number, _at, Author: null, Reason: null, RolledBackFrom: null);
}
