using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The two orders <see cref="BatchWrite"/> keeps apart: rows are locked in <b>id</b> order so two batches
/// whose id sets overlap cannot deadlock, and refusals are reported in <b>request</b> order so a caller's
/// row 3 is named as row 3.
/// </summary>
/// <remarks>
/// Asserted over the helper alone rather than only through a driver, because both are total orders over data
/// the caller supplies: the cheapest question that tells a correct implementation from a reversed one needs no
/// database at all. The port-level batch suite asserts what a caller is told; this asserts the mechanism that
/// decides it, including the <em>direction</em> — a descending sort is just as total and just as
/// deadlock-free, so "it sorts" is not the guarantee.
/// </remarks>
public class BatchWriteTests
{
    /// <summary>Locks are taken in ascending id order, whatever order the caller sent the rows in.</summary>
    [Fact]
    public void Rows_are_locked_in_ascending_id_order()
    {
        var ids = AscendingIds();

        var locked = BatchWrite.InLockOrder<Guid>([ids[2], ids[0], ids[1]], row => row);

        locked.Select(pair => pair.Row).ShouldBe([ids[0], ids[1], ids[2]], ignoreOrder: false);
    }

    /// <summary>
    /// The request position travels with the row, so the caller's own index survives the re-ordering.
    /// Reporting the sorted position would look like an index and point at a different row.
    /// </summary>
    [Fact]
    public void The_request_index_travels_with_the_row_it_names()
    {
        var ids = AscendingIds();

        var locked = BatchWrite.InLockOrder<Guid>([ids[2], ids[0], ids[1]], row => row);

        locked.Select(pair => pair.Index).ShouldBe([1, 2, 0], ignoreOrder: false);
    }

    /// <summary>
    /// Refusals collected in lock order leave in ascending request order, so a caller repairs the rows in the
    /// order they sent them.
    /// </summary>
    [Fact]
    public void Refusals_are_reported_in_ascending_request_order()
    {
        List<AlvoRowRefusal> collected = [Refusal(4), Refusal(0), Refusal(2)];

        var reported = BatchWrite.InRequestOrder(collected);

        reported.Select(refusal => refusal.Index).ShouldBe([0, 2, 4], ignoreOrder: false);
    }

    /// <summary>
    /// Every repeat after the first is named, by its own request index, and the first occurrence stands — so a
    /// caller removes exactly the rows the response points at.
    /// </summary>
    [Fact]
    public void Only_the_repeats_after_the_first_occurrence_are_named()
    {
        var ids = AscendingIds();

        var repeats = BatchWrite.RepeatedRows<Guid>([ids[0], ids[1], ids[0], ids[2], ids[0]], row => row, Refusal);

        repeats.Select(refusal => refusal.Index).ShouldBe([2, 4], ignoreOrder: false);
    }

    /// <summary>Three ids in ascending order, so a test can send them in any other one.</summary>
    private static IReadOnlyList<Guid> AscendingIds() =>
        [.. Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).OrderBy(id => id)];

    private static AlvoRowRefusal Refusal(int index) => new(index, "forbidden", "refused", null);
}
