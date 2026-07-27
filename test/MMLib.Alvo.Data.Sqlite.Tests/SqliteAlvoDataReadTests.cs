using MMLib.Alvo.Data.EntityFrameworkCore;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The read-path facts the inherited adversarial suite does not pin precisely enough: the
/// <em>statement</em>, not only the outcome.
/// </summary>
public sealed class SqliteAlvoDataReadTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task A_list_returns_only_the_rows_the_policy_predicate_admits()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        var mine = await world.QueryAsync(new AlvoQuery { Entity = "notes" }, world.Alice);

        mine.Count.ShouldBe(2);
        mine.ShouldAllBe(row => Equals(row["owner_id"], world.Alice.User.Value));
    }

    [Fact]
    public async Task A_query_with_no_context_throws_rather_than_defaulting_to_anyone()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await Should.ThrowAsync<ArgumentNullException>(
            () => world.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, null!, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(
            () => world.Data.GetAsync("notes", Guid.NewGuid(), null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_hidden_field_is_absent_from_every_returned_row_and_its_value_never_leaves_the_table()
    {
        var world = await AlvoDataWorlds.AccountsAsync(_fixture);

        var rows = await world.QueryAsync(new AlvoQuery { Entity = "accounts" }, world.Member);

        rows.ShouldAllBe(row => !row.Values.ContainsKey("secret"));
        world.LastStatement.ShouldContain("CAST(NULL AS TEXT) AS \"secret\"");
        world.LastStatement.ShouldNotContain(", \"secret\",");
    }

    /// <summary>
    /// One statement, the predicate inside its <c>WHERE</c>, and the whole text composed by Alvo rather than
    /// by EF: nothing is composed over the raw root, so EF has nothing to wrap it in a derived table for.
    /// That is what makes the ordering and the page boundary provably the same sequence.
    /// </summary>
    [Fact]
    public async Task The_policy_predicate_is_in_the_where_clause_of_exactly_one_statement()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await world.QueryAsync(
            new AlvoQuery
            {
                Entity = "notes",
                Filter = new AlvoComparison("title", AlvoFilterOperator.Like, "Alice%"),
                Sort = [new AlvoSort("label", Descending: true)],
                Limit = 1,
            },
            world.Alice);

        world.Statements.Count.ShouldBe(1);
        world.LastStatement.ShouldContain("\"owner_id\" = @alvo_u0");
        world.LastStatement.ShouldContain("\"title\" LIKE @alvo_f0");
        world.LastStatement.ShouldContain("ORDER BY CASE WHEN \"label\" IS NULL THEN 1 ELSE 0 END, \"label\" DESC, \"id\"");
        world.LastStatement.ShouldEndWith("LIMIT @alvo_limit");
        world.LastStatement.ShouldStartWith("SELECT \"id\"");
    }

    /// <summary>
    /// EF's default C# null semantics would compensate a <c>&lt;&gt;</c> with <c>OR … IS NULL</c> and
    /// return the null row. <see cref="AlvoFilterOperator"/> documents SQL's three-valued behaviour, and
    /// rendering the filter rather than composing it as LINQ is what delivers it.
    /// </summary>
    [Fact]
    public async Task A_neq_filter_does_not_match_a_null_field()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture, includeNullTitleRow: true);

        var rows = await world.QueryAsync(
            new AlvoQuery { Entity = "notes", Filter = new AlvoComparison("title", AlvoFilterOperator.Neq, "Alice-1") },
            world.Alice);

        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(row => row["title"] != null);
    }

    [Fact]
    public async Task A_page_after_a_cursor_continues_where_the_previous_page_stopped()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);
        var sort = new[] { new AlvoSort("label") };

        var first = await world.QueryAsync(new AlvoQuery { Entity = "notes", Sort = sort, Limit = 1 }, world.Alice);
        var cursor = DataWorld.CursorOf(first[^1]);
        var second = await world.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort, Limit = 1, After = cursor }, world.Alice);

        first.Count.ShouldBe(1);
        second.Count.ShouldBe(1);
        second[0]["id"].ShouldNotBe(first[0]["id"]);
        ((string)second[0]["label"]!).ShouldBeGreaterThan((string)first[0]["label"]!);
    }

    [Fact]
    public async Task A_forged_cursor_yields_an_empty_page_rather_than_the_first_one()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        var rows = await world.QueryAsync(
            new AlvoQuery { Entity = "notes", After = KeysetCursor.Encode(Guid.NewGuid()) }, world.Alice);

        rows.ShouldBeEmpty();
    }

    /// <summary>
    /// A cursor whose anchor row exists but belongs to another caller must find no anchor either — the
    /// anchor is re-read under the same policy predicate as the page, so a cross-tenant or cross-owner
    /// cursor is an empty page rather than an oracle for a row the caller cannot see.
    /// </summary>
    [Fact]
    public async Task A_cursor_naming_another_callers_row_yields_an_empty_page()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        var rows = await world.QueryAsync(
            new AlvoQuery { Entity = "notes", After = KeysetCursor.Encode(world.BobRowId) }, world.Alice);

        rows.ShouldBeEmpty();
    }

    /// <summary>
    /// A cursor is caller-supplied text, and <c>Base64Url.TryDecodeFromChars</c> throws on a non-alphabet
    /// character despite its name — so a garbage cursor must be an empty page, never an unhandled exception
    /// escaping a query.
    /// </summary>
    [Fact]
    public async Task A_garbage_cursor_is_an_empty_page_rather_than_a_thrown_format_error()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        var rows = await world.QueryAsync(
            new AlvoQuery { Entity = "notes", After = "not a cursor!!" }, world.Alice);

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_get_of_another_callers_row_reads_as_absent()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        (await world.GetAsync("notes", world.BobRowId, world.Alice)).ShouldBeNull();
        (await world.GetAsync("notes", Guid.NewGuid(), world.Alice)).ShouldBeNull();
    }

    /// <summary>
    /// The port promises the CLR types <c>AlvoRecord</c> documents on every engine. On SQLite that is only
    /// true because the row is read through EF's own type mapping — a raw reader over the identical
    /// statement returns <see cref="string"/> for the uuid, the decimal and the timestamp alike.
    /// </summary>
    [Fact]
    public async Task Every_mapped_field_reads_back_as_its_own_clr_type()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        var row = await world.GetAsync("vehicle", world.RowId, world.Alice);

        row.ShouldNotBeNull();
        row!["id"].ShouldBeOfType<Guid>();
        row["mileage"].ShouldBe(10L);
        row["price"].ShouldBe(9.99m);
        row["is_public"].ShouldBe(true);
        row["created_at"].ShouldBeOfType<DateTimeOffset>();
    }

    [Fact]
    public async Task A_negative_limit_is_refused_as_a_malformed_query_rather_than_sent_to_the_engine()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await Should.ThrowAsync<ArgumentException>(
            () => world.QueryAsync(new AlvoQuery { Entity = "notes", Limit = -1 }, world.Alice));
    }

    /// <summary>
    /// An entity the applied schema declares as dynamic is refused exactly like an unknown one, with the
    /// same message — F7 serves it by registering a dynamic dialect, never by branching in the data path.
    /// </summary>
    [Fact]
    public async Task A_read_of_an_undeclared_entity_is_refused_without_naming_it()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        var refused = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.QueryAsync(new AlvoQuery { Entity = "ghosts" }, world.Alice));

        refused.Message.ShouldNotContain("ghosts");
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
