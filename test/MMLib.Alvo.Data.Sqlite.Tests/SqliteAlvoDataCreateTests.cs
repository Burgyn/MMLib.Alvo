using Microsoft.EntityFrameworkCore;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// <c>create</c> is the one operation with no stored row to filter, so it carries no <c>USING</c> predicate
/// and its whole authorization is <c>WITH CHECK</c> over the candidate row — evaluated in memory, because
/// SQL cannot see a row that does not exist yet.
/// </summary>
public sealed class SqliteAlvoDataCreateTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task An_allowed_create_persists_and_is_readable_with_a_store_assigned_id()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);
        var payload = new Dictionary<string, object?>
        {
            ["owner_id"] = world.Alice.User.Value,
            ["tenant_id"] = world.Tenant.Value,
            ["title"] = "brand new",
            ["label"] = "new",
        };

        var created = await world.CreateAsync("notes", payload, world.Alice);

        created["id"].ShouldBeOfType<Guid>();
        created["title"].ShouldBe("brand new");
        var reread = await world.GetAsync("notes", (Guid)created["id"]!, world.Alice);
        reread!["title"].ShouldBe("brand new");
    }

    /// <summary>
    /// A create emits exactly two statements: the <c>INSERT</c>, and the re-read that produces what the port
    /// returns. The re-read goes through the same composed root every other read here does — a third statement,
    /// or a read composed some other way, would mean a row reached a caller through a path this data path does
    /// not control.
    /// </summary>
    /// <remarks>
    /// <c>create</c> carries no <c>USING</c> predicate — there is no stored row to filter when the decision is
    /// made — so what the re-read is constrained by is the synthesized tenant scope and the row id, which is
    /// what this asserts. Both are load-bearing: the id is the row this insert just wrote, and the tenant scope
    /// is the same term the candidate's post-image was already checked against.
    /// </remarks>
    [Fact]
    public async Task An_allowed_create_is_one_insert_and_one_re_read_through_the_composed_root()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await world.CreateAsync(
            "notes",
            new Dictionary<string, object?>
            {
                ["owner_id"] = world.Alice.User.Value,
                ["tenant_id"] = world.Tenant.Value,
                ["title"] = "one statement",
                ["label"] = "one",
            },
            world.Alice);

        world.Statements.Count.ShouldBe(2);
        world.Statements[0].ShouldStartWith("INSERT INTO \"notes\"");
        world.LastStatement.ShouldStartWith("SELECT");
        world.LastStatement.ShouldContain("\"tenant_id\" = @alvo_t0");
        world.LastStatement.ShouldContain("\"id\" = @alvo_id");
    }

    /// <summary>
    /// The record a create returns is the row the database holds, not the payload the caller sent — so a
    /// column the caller never mentioned comes back with the value the store assigned it. Before this, a 201
    /// had no <c>ETag</c> source, every database default was missing, and PR6's <c>computed</c> column would
    /// have been absent from a create response entirely.
    /// </summary>
    [Fact]
    public async Task A_created_record_carries_what_the_store_assigned_not_what_the_caller_sent()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        var created = await world.CreateAsync(
            "notes",
            new Dictionary<string, object?>
            {
                ["owner_id"] = world.Alice.User.Value,
                ["tenant_id"] = world.Tenant.Value,
                ["label"] = "re-read",
            },
            world.Alice);

        created.Values.Keys.ShouldContain("title");
        created["title"].ShouldBeNull();
        created["label"].ShouldBe("re-read");
    }

    [Fact]
    public async Task A_create_whose_post_image_fails_the_check_writes_nothing()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);
        var payload = new Dictionary<string, object?>
        {
            ["owner_id"] = world.Bob.User.Value,
            ["tenant_id"] = world.Tenant.Value,
            ["title"] = "smuggled",
        };

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.CreateAsync("notes", payload, world.Alice));

        var rows = await world.QueryAsync(new AlvoQuery { Entity = "notes" }, world.Bob);
        rows.ShouldAllBe(row => !Equals(row["title"], "smuggled"));
    }

    /// <summary>
    /// The check is refused before anything is written, so the refused create leaves no statement behind at
    /// all — a write-then-rollback would have emitted the <c>INSERT</c>.
    /// </summary>
    [Fact]
    public async Task A_refused_create_never_reaches_the_engine()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.CreateAsync(
            "notes",
            new Dictionary<string, object?> { ["owner_id"] = world.Bob.User.Value, ["title"] = "smuggled" },
            world.Alice));

        world.Statements.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_hidden_field_is_absent_from_the_record_the_create_returns()
    {
        var world = await AlvoDataWorlds.AccountsAsync(_fixture);

        var created = await world.CreateAsync(
            "accounts", new Dictionary<string, object?> { ["title"] = "New", ["secret"] = "shh" }, world.Member);

        created.Values.ContainsKey("secret").ShouldBeFalse();
        created["title"].ShouldBe("New");
    }

    /// <summary>
    /// A <c>hidden</c> field is still written, even though it is stripped from the response: <c>hidden</c>
    /// restricts reading. The admin the field is visible to reads back what the member wrote.
    /// </summary>
    [Fact]
    public async Task A_hidden_field_is_written_even_though_the_response_does_not_carry_it()
    {
        var world = await AlvoDataWorlds.AccountsAsync(_fixture);

        var created = await world.CreateAsync(
            "accounts", new Dictionary<string, object?> { ["title"] = "New", ["note"] = "kept" }, world.Member);
        var asAdmin = await world.GetAsync("accounts", (Guid)created["id"]!, world.Admin);

        asAdmin!["note"].ShouldBe("kept");
    }

    [Fact]
    public async Task A_caller_supplied_row_id_is_refused_rather_than_honoured()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);
        var payload = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid(),
            ["owner_id"] = world.Alice.User.Value,
            ["tenant_id"] = world.Tenant.Value,
            ["label"] = "smuggled-id",
        };

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.CreateAsync("notes", payload, world.Alice));
    }

    /// <summary>
    /// An explicit <see langword="null"/> in a create payload leaves the column at its database default,
    /// which for a nullable column is <c>NULL</c> — indistinguishable from an omitted key, and correct for a
    /// fresh row. On an update it would not be, which is why that path uses <c>ExecuteUpdate</c> setters.
    /// </summary>
    [Fact]
    public async Task An_explicit_null_in_a_create_payload_leaves_the_column_null()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        var created = await world.CreateAsync(
            "notes",
            new Dictionary<string, object?>
            {
                ["owner_id"] = world.Alice.User.Value,
                ["tenant_id"] = world.Tenant.Value,
                ["title"] = null,
                ["label"] = "nulled",
            },
            world.Alice);

        var reread = await world.GetAsync("notes", (Guid)created["id"]!, world.Alice);
        reread!["title"].ShouldBeNull();
    }

    /// <summary>
    /// The database's own <c>NOT NULL</c> is still the required-ness gate, even though the read model
    /// marks every property optional. A missing required value must surface as a refusal, not as a row.
    /// </summary>
    /// <remarks>
    /// <see cref="DbUpdateException"/> is deliberately <em>not</em> one of <c>IAlvoData</c>'s declared
    /// exceptions: schema-derived request validation belongs above this port, and this port does not promise
    /// to pre-validate types or required-ness. Pinned here as the rough edge that layer closes.
    /// </remarks>
    [Fact]
    public async Task A_missing_required_value_is_refused_by_the_database_constraint()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        await Should.ThrowAsync<DbUpdateException>(() => world.CreateAsync(
            "vehicle",
            new Dictionary<string, object?> { ["tenant_id"] = world.Tenant.Value, ["owner_id"] = world.Alice.User.Value },
            world.Alice));
    }

    [Fact]
    public async Task A_create_into_an_entity_with_no_create_rule_is_denied()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.CreateAsync(
            "ghosts", new Dictionary<string, object?> { ["title"] = "x" }, world.Alice));
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
