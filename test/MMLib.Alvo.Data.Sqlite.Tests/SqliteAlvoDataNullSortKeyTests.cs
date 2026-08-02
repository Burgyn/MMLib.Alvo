namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// A keyset boundary is a chain of comparisons with no <c>IS NULL</c> arm, so a <c>NULL</c> on either side
/// makes the whole term <c>NULL</c> and a <c>WHERE</c> treats that as false. Paging over a nullable sort key
/// therefore stopped early and silently — with <c>nullslast</c> the null-keyed tail was unreachable, and with
/// <c>nullsfirst</c> the very first page's anchor had a null key, so page two was empty.
/// </summary>
/// <remarks>
/// The milestone design's ruling is that a nullable sort column must declare its null placement <b>or be
/// rejected</b>; shipping the third option — accept the query and lose rows — is what these facts forbid. The
/// refusal is on the port's malformed-query channel, not an authorization refusal: the field is one the caller
/// can read and nothing is being hidden. The <c>IS NULL</c>-aware boundary that would make such a page work is
/// PR3's, which owns the paging surface and the cursor contract.
/// </remarks>
public sealed class SqliteAlvoDataNullSortKeyTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Theory]
    [InlineData(AlvoNullPlacement.Last)]
    [InlineData(AlvoNullPlacement.First)]
    public async Task A_limited_read_over_a_nullable_sort_key_is_refused_rather_than_losing_the_null_rows(
        AlvoNullPlacement nulls)
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture, includeNullTitleRow: true);

        await Should.ThrowAsync<ArgumentException>(() => world.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title", Nulls: nulls)], Limit = 1 },
            world.Alice));
    }

    [Theory]
    [InlineData(AlvoNullPlacement.Last)]
    [InlineData(AlvoNullPlacement.First)]
    public async Task A_cursored_read_over_a_nullable_sort_key_is_refused_too(AlvoNullPlacement nulls)
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture, includeNullTitleRow: true);

        await Should.ThrowAsync<ArgumentException>(() => world.QueryAsync(
            new AlvoQuery
            {
                Entity = "notes",
                Sort = [new AlvoSort("title", Nulls: nulls)],
                After = KeysetCursorForAnyRow(world),
            },
            world.Alice));
    }

    /// <summary>
    /// An unpaged sorted read has no boundary at all, so its order is already correct over nulls — refusing it
    /// would break whole-set reads for no gain. The null-keyed row is returned, and it is returned last.
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
    /// A paged read over a <b>non-nullable</b> sort key is what the refusal must not touch — the boundary is
    /// sound there, and the decimal paging suite depends on it.
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
    /// The refusal must not become a schema oracle: a paged read naming a field the entity does not declare
    /// still gets the authorization refusal every other unavailable field gets, not the nullability one.
    /// </summary>
    [Fact]
    public async Task A_paged_read_over_an_undeclared_sort_key_is_still_an_authorization_refusal()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("nope")], Limit = 1 }, world.Alice));
    }

    private static string KeysetCursorForAnyRow(DataWorld world) =>
        EntityFrameworkCore.KeysetCursor.Encode(world.AliceRowId);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
