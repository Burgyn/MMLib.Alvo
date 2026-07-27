namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// A caller filter's value reaches the engine through the <b>column's</b> own type mapping, not through the
/// mapping for whatever CLR type it happened to arrive as. Every fact here goes through
/// <see cref="IAlvoData.QueryAsync"/> — the live path — because the previous shape of this guarantee was
/// implemented on an overload the data path never called, so a suite that bound directly could not see it.
/// </summary>
/// <remarks>
/// On SQLite the difference is invisible until it costs a row: a <c>uuid</c> is upper-case <c>TEXT</c>, a
/// timestamp is <c>'yyyy-MM-dd HH:mm:ss'</c> with a space rather than a <c>T</c>, and a <c>date</c> is a bare
/// calendar day. A value bound as raw text is compared lexically against those, so it silently matches
/// nothing — fail-closed under a positive comparison and fail-open under a negated one.
/// </remarks>
public sealed class SqliteAlvoDataFilterBindingTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task A_timestamp_column_matches_the_same_instant_written_as_text()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        var rows = await Filtered(world, "created_at", AlvoFilterOperator.Gte, "1970-01-02T00:00:00");

        rows.Count.ShouldBe(1);
    }

    /// <summary>
    /// The same instant one hour later must <em>not</em> match, so the fact above cannot pass by binding
    /// something that matches everything.
    /// </summary>
    [Fact]
    public async Task A_timestamp_column_does_not_match_a_later_instant_written_as_text()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        var rows = await Filtered(world, "created_at", AlvoFilterOperator.Gt, "1970-01-02T01:00:00");

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_uuid_column_matches_the_same_id_written_as_text()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        var rows = await Filtered(
            world, "owner_id", AlvoFilterOperator.Eq, world.Alice.User.Value.ToString());

        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_date_column_matches_the_same_day_written_as_text()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        var rows = await Filtered(world, "due_on", AlvoFilterOperator.Eq, "2026-01-02");

        rows.Count.ShouldBe(1);
    }

    /// <summary>
    /// <c>Convert.ChangeType</c> rounds a fractional value into an integral column rather than refusing it, so
    /// <c>mileage &gt; 12.7</c> would answer <c>mileage &gt; 13</c> and drop the row with <c>mileage = 13</c>
    /// from a request whose stated predicate included it. Refused, and refused on the live path.
    /// </summary>
    [Fact]
    public async Task A_fractional_bound_against_an_integral_column_is_refused_rather_than_rounded()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        await Should.ThrowAsync<InvalidOperationException>(
            () => Filtered(world, "mileage", AlvoFilterOperator.Gt, 12.7));
    }

    [Fact]
    public async Task A_whole_number_of_another_numeric_type_still_binds_against_an_integral_column()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        var rows = await Filtered(world, "mileage", AlvoFilterOperator.Gte, 10m);

        rows.Count.ShouldBe(1);
    }

    /// <summary>
    /// A value the column cannot hold is refused loudly rather than coerced into something plausible: a
    /// wrong-but-plausible value is exactly the silent miss this binding exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_value_the_column_cannot_hold_is_refused_loudly()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        await Should.ThrowAsync<InvalidOperationException>(
            () => Filtered(world, "owner_id", AlvoFilterOperator.Eq, "not-a-uuid"));
    }

    /// <summary>
    /// The keyset cursor's boundary compares the anchor row's own values, which arrive from a previous read
    /// already shaped by EF's mapping — so the cursor path must bind through the column too, or a second page
    /// over a timestamp or uuid key would compare a repaired column against raw text.
    /// </summary>
    [Fact]
    public async Task A_cursor_over_a_timestamp_key_pages_rather_than_stopping()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture, ("ACME-002", DateTimeOffset.UnixEpoch.AddDays(2)));
        var sort = new[] { new AlvoSort("created_at") };

        var first = await world.QueryAsync(
            new AlvoQuery { Entity = "vehicle", Sort = sort, Limit = 1 }, world.Alice);
        var second = await world.QueryAsync(
            new AlvoQuery
            {
                Entity = "vehicle",
                Sort = sort,
                Limit = 1,
                After = DataWorld.CursorOf(first[^1]),
            },
            world.Alice);

        first.Count.ShouldBe(1);
        second.Count.ShouldBe(1);
        second[0]["id"].ShouldNotBe(first[0]["id"]);
    }

    private static Task<IReadOnlyList<AlvoRecord>> Filtered(
        DataWorld world, string field, AlvoFilterOperator op, object? value) => world.QueryAsync(
            new AlvoQuery { Entity = "vehicle", Filter = new AlvoComparison(field, op, value) }, world.Alice);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
