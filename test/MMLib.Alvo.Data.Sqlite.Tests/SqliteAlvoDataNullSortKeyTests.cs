namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Paging over a nullable sort key, on a real engine. The boundary used to be a chain of comparisons with no
/// <c>IS NULL</c> arm, so a <c>NULL</c> on either side made the whole term <c>NULL</c> and a <c>WHERE</c>
/// treated that as false: with <c>nullslast</c> the null-keyed tail was unreachable, and with
/// <c>nullsfirst</c> the very first page's anchor had a null key, so page two came back empty. F3 refused
/// such a read; F4 answers it, because the boundary now compares the same <em>(rank, value)</em> pair
/// <c>SortSqlRenderer</c> orders by.
/// </summary>
/// <remarks>
/// The port-level walk lives in the inherited <c>AlvoDataPagingTests</c>, over a fixture built for it. What
/// this suite adds is the engine: SQLite really executing the emitted <c>CASE WHEN … IS NULL</c> rank beside
/// the emitted boundary, over a column whose <c>NULL</c> is a stored <c>NULL</c> rather than a C# field. The
/// two facts that survive unchanged from the refusal era are the last two — a required key and an undeclared
/// key must still behave exactly as they did, since neither is what changed.
/// </remarks>
public sealed class SqliteAlvoDataNullSortKeyTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <summary>
    /// The direct inverse of the measured defect: three visible rows, one of them null-keyed, walked one page
    /// at a time — all three come out, in the unpaged order, under both placements.
    /// </summary>
    [Theory]
    [InlineData(AlvoNullPlacement.Last)]
    [InlineData(AlvoNullPlacement.First)]
    public async Task A_limited_read_over_a_nullable_sort_key_walks_out_every_row(AlvoNullPlacement nulls)
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture, includeNullTitleRow: true);
        var sort = new[] { new AlvoSort("title", Nulls: nulls) };
        var unpaged = await world.QueryAsync(new AlvoQuery { Entity = "notes", Sort = sort }, world.Alice);

        var walked = await WalkAsync(world, sort);

        unpaged.Count.ShouldBe(3);
        walked.ShouldBe([.. unpaged.Select(row => row["id"])]);
    }

    /// <summary>
    /// The placement is honoured rather than merely survived: the null-keyed row is first under
    /// <c>nullsfirst</c> and last under <c>nullslast</c>, in the paged read as well as the unpaged one.
    /// </summary>
    [Theory]
    [InlineData(AlvoNullPlacement.Last, 2)]
    [InlineData(AlvoNullPlacement.First, 0)]
    public async Task The_null_keyed_row_is_paged_into_the_position_its_placement_names(
        AlvoNullPlacement nulls, int expectedIndex)
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture, includeNullTitleRow: true);

        var walked = await PagesAsync(world, [new AlvoSort("title", Nulls: nulls)]);

        walked[expectedIndex]["title"].ShouldBeNull();
    }

    /// <summary>
    /// A cursor whose <b>anchor row's own key is null</b> — the case that returned an empty page under
    /// <c>nullsfirst</c>, because the boundary compared against a <c>NULL</c> parameter and matched nothing.
    /// Asked directly rather than only as a step of the walk above, since it is the one anchor shape the F3
    /// renderer could not express at all.
    /// </summary>
    [Fact]
    public async Task A_cursor_anchored_on_a_null_keyed_row_still_returns_the_rows_after_it()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture, includeNullTitleRow: true);
        var sort = new[] { new AlvoSort("title", Nulls: AlvoNullPlacement.First) };

        var first = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort, Limit = 1 }, world.Alice, Ct);
        var second = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort, Limit = 2, After = first.NextCursor }, world.Alice, Ct);

        first.Items.ShouldHaveSingleItem()["title"].ShouldBeNull();
        second.Items.Count.ShouldBe(2);
        second.Items.ShouldAllBe(row => row["title"] != null);
    }

    /// <summary>
    /// An unpaged sorted read had no boundary and was always legal; it must still answer, and still put the
    /// null row where the placement says.
    /// </summary>
    [Fact]
    public async Task An_unpaged_read_over_a_nullable_sort_key_stays_legal_and_returns_the_null_row()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture, includeNullTitleRow: true);

        var rows = await world.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")] }, world.Alice);

        rows.Count.ShouldBe(3);
        rows[^1]["title"].ShouldBeNull();
    }

    /// <summary>
    /// A paged read over a <b>non-nullable</b> sort key is the shape that never changed — the boundary is the
    /// same nested-OR expansion it always was, and the decimal paging suite depends on it.
    /// </summary>
    [Fact]
    public async Task A_limited_read_over_a_required_sort_key_is_still_allowed()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        var rows = await world.QueryAsync(
            new AlvoQuery { Entity = "vehicle", Sort = [new AlvoSort("plate")], Limit = 1 }, world.Alice);

        rows.Count.ShouldBe(1);
    }

    /// <summary>
    /// Nullability decides the boundary's <em>shape</em>, never whether the field is reachable: a paged read
    /// naming a field the entity does not declare still gets the authorization refusal every other
    /// unavailable field gets.
    /// </summary>
    [Fact]
    public async Task A_paged_read_over_an_undeclared_sort_key_is_still_an_authorization_refusal()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("nope")], Limit = 1 }, world.Alice));
    }

    private static async Task<List<object?>> WalkAsync(DataWorld world, IReadOnlyList<AlvoSort> sort) =>
        [.. (await PagesAsync(world, sort)).Select(row => row["id"])];

    /// <summary>
    /// Walks the whole visible set one row per page, following each page's own cursor — so every row in turn
    /// is the anchor, the null-keyed one included.
    /// </summary>
    private static async Task<List<AlvoRecord>> PagesAsync(DataWorld world, IReadOnlyList<AlvoSort> sort)
    {
        var walked = new List<AlvoRecord>();
        string? cursor = null;
        for (var page = 0; page < 8; page++)
        {
            var current = await world.Data.QueryAsync(
                new AlvoQuery { Entity = "notes", Sort = sort, Limit = 1, After = cursor }, world.Alice, Ct);
            walked.AddRange(current.Items);
            if (current.NextCursor is null)
            {
                return walked;
            }

            cursor = current.NextCursor;
        }

        throw new InvalidOperationException("A cursor is not advancing: the walk issued more pages than rows.");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
