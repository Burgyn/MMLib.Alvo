using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The diff the preview screen renders.
/// </summary>
/// <remarks>
/// It is the only thing between an operator and applying a change they have not read, so its
/// failure mode is not a cosmetic one: a diff that drops a line is a change nobody reviewed.
/// </remarks>
public class LineDiffTests
{
    [Fact]
    public void Two_identical_documents_have_no_diff() =>
        LineDiff.Between("{\n  \"a\": 1\n}", "{\n  \"a\": 1\n}").ShouldBeEmpty();

    [Fact]
    public void A_changed_line_is_a_removal_and_an_addition()
    {
        var diff = LineDiff.Between("{\n  \"max\": 160\n}", "{\n  \"max\": 200\n}");

        diff.Where(line => line.Change == LineChange.Removed).Select(line => line.Text.Trim())
            .ShouldBe(["\"max\": 160"]);
        diff.Where(line => line.Change == LineChange.Added).Select(line => line.Text.Trim())
            .ShouldBe(["\"max\": 200"]);
    }

    [Fact]
    public void An_added_line_is_not_reported_as_a_removal()
    {
        var diff = LineDiff.Between("a\nb", "a\nnew\nb");

        diff.Count(line => line.Change == LineChange.Removed).ShouldBe(0);
        diff.Single(line => line.Change == LineChange.Added).Text.ShouldBe("new");
    }

    [Fact]
    public void A_removed_line_is_not_reported_as_an_addition()
    {
        var diff = LineDiff.Between("a\ngone\nb", "a\nb");

        diff.Count(line => line.Change == LineChange.Added).ShouldBe(0);
        diff.Single(line => line.Change == LineChange.Removed).Text.ShouldBe("gone");
    }

    /// <summary>
    /// A descriptor is hundreds of lines and a facet change touches one, so the unchanged stretches
    /// are elided — a diff nobody scrolls is a diff nobody reads.
    /// </summary>
    [Fact]
    public void Untouched_stretches_are_elided()
    {
        var before = string.Join('\n', Enumerable.Range(0, 60).Select(index => $"line {index}"));
        var after = before.Replace("line 30", "line thirty", StringComparison.Ordinal);

        var diff = LineDiff.Between(before, after);

        diff.Count.ShouldBeLessThan(20);
        diff.Count(line => line.Text == LineDiff.Elision).ShouldBe(2);
        diff.Single(line => line.Change == LineChange.Added).Text.ShouldBe("line thirty");
    }

    /// <summary>
    /// Both line endings reach this from the same places a descriptor does — a file on disk, an
    /// editor's paste buffer, a database column — and a diff that reported every line as changed
    /// because of them would be worse than none.
    /// </summary>
    [Fact]
    public void Line_endings_are_not_a_change() =>
        LineDiff.Between("a\r\nb\r\nc", "a\nb\nc").ShouldBeEmpty();

    [Fact]
    public void An_addition_carries_its_line_number_in_the_new_document() =>
        LineDiff.Between("a\nb", "a\nnew\nb")
            .Single(line => line.Change == LineChange.Added).Number.ShouldBe(2);

    /// <summary>
    /// One change in a long descriptor builds no table the size of both (final review M18): History opens a diff on
    /// every revision click, on a shared server, and the lines both documents begin and end with need none.
    /// </summary>
    [Fact]
    public void One_change_in_a_long_document_costs_what_the_change_does()
    {
        var before = Numbered(5000);
        var after = before.Replace("line 2500\n", "line 2500 changed\n", StringComparison.Ordinal);

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var diff = LineDiff.Between(before, after);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        allocated.ShouldBeLessThan(4 * 1024 * 1024, "a 5 000 by 5 000 table is 100 MB");
        diff.Single(line => line.Change == LineChange.Added).ShouldBe(new DiffLine(LineChange.Added, "line 2500 changed", 2501));
        diff.Single(line => line.Change == LineChange.Removed).Number.ShouldBe(2501);
        diff.First(line => line.Change == LineChange.Same && line.Text != LineDiff.Elision).Number.ShouldBe(2498);
    }

    /// <summary>
    /// Two long documents with nothing in common are shown as the one removed and the other added, never aligned by
    /// a table past <see cref="LineDiff.MaxCells"/> cells: the answer is still a correct diff, only not the shortest.
    /// </summary>
    [Fact]
    public void Two_long_documents_with_nothing_in_common_are_capped()
    {
        var before = Numbered(3000);
        var after = before.Replace("line", "row", StringComparison.Ordinal);

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var diff = LineDiff.Between(before, after);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        allocated.ShouldBeLessThan(8 * 1024 * 1024, "a 3 000 by 3 000 table is 36 MB");
        diff.Count(line => line.Change == LineChange.Removed).ShouldBe(3000);
        diff.Count(line => line.Change == LineChange.Added).ShouldBe(3000);
    }

    /// <summary>A diff past the cap says it was not aligned; one under it, and an identical pair, say it was.</summary>
    [Fact]
    public void A_diff_says_whether_it_was_aligned()
    {
        LineDiff.Compare(Numbered(3000), Numbered(3000).Replace("line", "row", StringComparison.Ordinal)).Aligned.ShouldBeFalse();
        LineDiff.Compare("a\nb", "a\nc").Aligned.ShouldBeTrue();
        LineDiff.Compare("a", "a").ShouldBe(new LineDiffResult([], true), "identical: nothing to draw, nothing capped");
    }

    /// <summary>A change right at the start and one right at the end are found with the trimming in place.</summary>
    [Fact]
    public void Changes_at_both_ends_are_found()
    {
        var diff = LineDiff.Between("a\nb\nc\nd", "A\nb\nc\nD");

        diff.Where(line => line.Change == LineChange.Removed).Select(line => line.Text).ShouldBe(["a", "d"]);
        diff.Where(line => line.Change == LineChange.Added).Select(line => (line.Text, line.Number))
            .ShouldBe([("A", 1), ("D", 4)]);
    }

    private static string Numbered(int lines)
        => string.Concat(Enumerable.Range(0, lines).Select(index => $"line {index}\n"));
}
