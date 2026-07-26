using System.Globalization;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The one place a page's <em>order</em> and a page's <em>boundary</em> can disagree, and the reason the
/// <c>ORDER BY</c> is rendered into the read statement instead of composed in LINQ.
/// </summary>
/// <remarks>
/// <para>
/// SQLite has no decimal storage class: EF maps a <c>decimal</c> field to a <c>TEXT</c> column whose
/// lexical order is not its numeric order (<c>"10.0" &lt; "2.0"</c>). Three orderings are available over
/// such a column, and they are not interchangeable — the raw text, EF's own exact <c>EF_DECIMAL</c>
/// collation (what a LINQ <c>OrderBy</c> emits), and the driver's <c>CAST(… AS REAL)</c> repair (what
/// <c>IFieldSqlRenderer.RenderComparableOperands</c>, and therefore the keyset boundary, uses).
/// </para>
/// <para>
/// A keyset page is correct only while its order and its boundary describe the <b>same</b> sequence, so both
/// come from that one seam. These facts fail if either side loses the repair: with a lexical
/// <c>ORDER BY</c> against a numeric boundary the first page starts at <c>10</c> and the walk ends two rows
/// early; with a numeric <c>ORDER BY</c> against a lexical boundary it starts at <c>2</c> and ends after
/// <c>9</c>. Sorting a single page would catch neither.
/// </para>
/// </remarks>
public sealed class SqliteAlvoDataDecimalPagingTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    private static readonly decimal[] _lexicallyMisleadingPrices = [2m, 9m, 10m, 100m];

    [Fact]
    public async Task A_decimal_sort_orders_by_value_rather_than_by_its_text_representation()
    {
        var world = await PricedWorldAsync(_lexicallyMisleadingPrices);

        var rows = await world.QueryAsync(
            new AlvoQuery { Entity = "vehicle", Sort = [new AlvoSort("price")] }, world.Alice);

        Prices(rows).ShouldBe(_lexicallyMisleadingPrices);
    }

    /// <summary>
    /// Walking every page one row at a time is what makes an order/boundary disagreement visible: each page
    /// re-reads its anchor's key value and asks the engine for the next row past it, so a boundary computed
    /// in a different order than the page's own skips or repeats rows rather than mis-sorting them.
    /// </summary>
    [Fact]
    public async Task Paging_one_row_at_a_time_over_a_decimal_key_neither_skips_nor_repeats_a_row()
    {
        var world = await PricedWorldAsync(_lexicallyMisleadingPrices);

        var walked = await WalkAsync(world, new AlvoSort("price"));

        Prices(walked).ShouldBe(_lexicallyMisleadingPrices);
    }

    [Fact]
    public async Task Paging_descending_over_a_decimal_key_walks_the_same_rows_in_reverse()
    {
        var world = await PricedWorldAsync(_lexicallyMisleadingPrices);

        var walked = await WalkAsync(world, new AlvoSort("price", Descending: true));

        Prices(walked).ShouldBe(_lexicallyMisleadingPrices.Reverse());
    }

    /// <summary>
    /// The edge the driver's <c>REAL</c> repair is documented to have: past 53 bits of mantissa two distinct
    /// decimals round to one double. That is <em>fine</em> as long as the order and the boundary agree it is
    /// a tie and fall through to the row-key tie-breaker — which is exactly what a single seam guarantees and
    /// what an exactly-collating LINQ <c>ORDER BY</c> would break, since it separates rows the boundary ties.
    /// </summary>
    [Fact]
    public async Task Two_prices_that_collide_in_the_repaired_space_are_still_both_walked()
    {
        var lower = 999999999999999.51m;
        var higher = 999999999999999.55m;
        ((double)lower).ShouldBe((double)higher);

        var world = await PricedWorldAsync([lower, higher]);

        var walked = await WalkAsync(world, new AlvoSort("price"));

        walked.Count.ShouldBe(2);
        walked.Select(row => row["id"]).Distinct().Count().ShouldBe(2);
    }

    private Task<DataWorld> PricedWorldAsync(IReadOnlyList<decimal> prices) =>
        AlvoDataWorlds.PricedVehicleAsync(_fixture, prices);

    /// <summary>
    /// Pages one row at a time until the walk runs dry, refusing to loop forever if a boundary ever fails to
    /// advance — a repeated row would otherwise hang the test instead of failing it.
    /// </summary>
    private static async Task<IReadOnlyList<AlvoRecord>> WalkAsync(DataWorld world, AlvoSort sort)
    {
        var walked = new List<AlvoRecord>();
        string? cursor = null;

        for (var page = 0; page <= MaxPages; page++)
        {
            var rows = await world.QueryAsync(
                new AlvoQuery { Entity = "vehicle", Sort = [sort], Limit = 1, After = cursor }, world.Alice);
            if (rows.Count == 0)
            {
                return walked;
            }

            walked.Add(rows[0]);
            cursor = DataWorld.CursorOf(rows[0]);
        }

        throw new InvalidOperationException(
            $"The walk did not terminate within {MaxPages} pages, so a page boundary is repeating a row.");
    }

    private const int MaxPages = 20;

    private static IReadOnlyList<decimal> Prices(IEnumerable<AlvoRecord> rows) =>
        [.. rows.Select(row => Convert.ToDecimal(row["price"], CultureInfo.InvariantCulture))];

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
