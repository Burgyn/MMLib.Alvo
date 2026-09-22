using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests;

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
}
