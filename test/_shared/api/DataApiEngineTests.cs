using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The HTTP Data API's engine-sensitive facts, declared once and run against every engine Alvo ships a
/// driver for — CRUD, filtering, keyset paging, <c>ETag</c>/<c>If-Match</c> and <c>Idempotency-Key</c>, each
/// over real HTTP against a real store.
/// </summary>
/// <remarks>
/// <para>
/// It exists because #19's DoD is "tests green on SQLite + Postgres" and, until Task 9, only the
/// <em>port-level</em> suites had a PostgreSQL leg — every API-level fact ran on SQLite alone. The
/// difference is not academic: what an engine does to a <em>stored value</em> is invisible to the port suites
/// but decides the HTTP contract. <see cref="A_get_of_an_audited_entity_carries_a_strong_etag"/> is the case
/// in point — <c>RowVersionETag.For</c> mints a tag only when the stored <c>updated_at</c> materializes as a
/// <see cref="DateTimeOffset"/>, so an engine whose driver handed back a <see cref="DateTime"/> would make
/// <b>every ETag on that engine silently vanish</b> and optimistic concurrency with it.
/// </para>
/// <para>
/// The facts here are chosen for exactly that property: each one either reads a value back out of storage, or
/// depends on an order or a comparison the engine performs. Facts that are purely about the request layer —
/// the route table, the query-string grammar, the problem documents, the OpenAPI document — deliberately stay
/// in the SQLite-only suites, because running them a second time on PostgreSQL costs a container and proves
/// nothing new.
/// </para>
/// <para>
/// The shape mirrors the port-level suites (<c>MMLib.Alvo.Testing.Data.AlvoDataPagingTests</c> and friends):
/// an abstract seam, one concrete subclass per engine. Here the seam is <see cref="Engine"/> and the subclasses
/// are <c>SqliteDataApiTests</c> (ring0, no Docker) and <c>DataApiOnPostgresTests</c> (ring2, Testcontainers).
/// </para>
/// </remarks>
public abstract class DataApiEngineTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// A second admin, so a race has two <em>distinguishable</em> callers: with one key, neither write in the
    /// lost-update fact could be attributed to a particular caller.
    /// </summary>
    private static readonly TestApiKey _other = new("other-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>The engine every world below runs on.</summary>
    /// <remarks>
    /// The engine, not a world: each fact needs its own empty database, because several of them assert exact
    /// row counts read straight off a table.
    /// </remarks>
    protected abstract AlvoApiEngine Engine { get; }

    /// <summary>A create is stored, and is the row a following read hands back.</summary>
    /// <remarks>
    /// Three claims, because a create can fail in three directions that a 201 alone cannot separate: the row
    /// is in the table (counted off the table, not off a list a policy filters), the id in the body addresses
    /// it, and the values that come back are the values that went in — which is the half an engine's type
    /// mapping can break on its own.
    /// </remarks>
    [Fact]
    public async Task A_create_is_stored_and_a_get_reads_back_what_was_sent()
    {
        await using var world = await StartAsync();

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: Owner("Stored Ltd", "stored@example.com"));
        var id = await IdOfAsync(created);
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/owners/{id}", _admin);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await world.CountRowsAsync("owners")).ShouldBe(1, "the create must have reached the table");
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.ReadTextAsync());
        var body = await read.ReadJsonObjectAsync();
        body["name"]!.GetValue<string>().ShouldBe("Stored Ltd");
        body["email"]!.GetValue<string>().ShouldBe("stored@example.com");
    }

    /// <summary>
    /// A PATCH writes the field it names and leaves every other one alone.
    /// </summary>
    /// <remarks>
    /// The untouched fields are the load-bearing half: <c>UpdateAsync</c> is partial by contract, and a
    /// whole-row replacement would answer 200 while nulling the two columns the payload did not mention.
    /// A fact that only checked the renamed field could not tell those apart.
    /// </remarks>
    [Fact]
    public async Task A_patch_writes_the_field_it_names_and_leaves_the_others_alone()
    {
        await using var world = await StartAsync();
        var id = await CreateOwnerAsync(world, Owner("Before Ltd", "keep@example.com", "+421900000000"));

        using var patched = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{id}", _admin, body: new JsonObject { ["name"] = "After Ltd" });

        patched.StatusCode.ShouldBe(HttpStatusCode.OK, await patched.ReadTextAsync());
        var body = await ReadOwnerAsync(world, id);
        body["name"]!.GetValue<string>().ShouldBe("After Ltd");
        body["email"]!.GetValue<string>().ShouldBe("keep@example.com", "a partial update must not clear a field it never named");
        body["phone"]!.GetValue<string>().ShouldBe("+421900000000", "nor a second one");
    }

    /// <summary>A DELETE removes the row: the table is empty and a following read is a 404.</summary>
    /// <remarks>
    /// The count is read off the table rather than from a list, because a list is already filtered by the
    /// caller's policy — an empty page cannot tell "the row is gone" from "you may not see it".
    /// </remarks>
    [Fact]
    public async Task A_delete_removes_the_row_and_a_following_get_is_404()
    {
        await using var world = await StartAsync();
        var id = await CreateOwnerAsync(world, Owner("Doomed Ltd"));

        using var deleted = await world.SendAsync(HttpMethod.Delete, $"/api/owners/{id}", _admin);
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/owners/{id}", _admin);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deleted.ReadTextAsync());
        (await world.CountRowsAsync("owners")).ShouldBe(0, "the row must be gone from the table, not merely hidden");
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A filter narrows the rows a real store returns, and the unfiltered read proves the narrowing was the
    /// filter's work rather than an empty table's.
    /// </summary>
    [Fact]
    public async Task A_filter_narrows_the_rows_the_store_returns()
    {
        await using var world = await StartAsync();
        await SeedVehiclesAsync(world);

        using var filtered = await world.SendAsync(HttpMethod.Get, "/api/vehicles?make=eq.vw", _admin);
        using var everything = await world.SendAsync(HttpMethod.Get, "/api/vehicles", _admin);

        (await filtered.ReadFieldAsync("make")).ShouldBe(["vw"]);
        (await everything.ReadFieldAsync("make")).Count.ShouldBe(3, "or the filtered read proves nothing");
    }

    /// <summary>
    /// A filter over an integer field compares numerically, which is a claim about the engine's own storage
    /// and comparison rather than about the parser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one fact in this file whose seeded values are chosen against a specific wrong answer: as text every
    /// seeded year sorts below <c>'500'</c>, so an engine (or a driver) that compared the column as text
    /// returns nothing at all. That is not hypothetical for SQLite, whose storage is dynamically typed.
    /// </para>
    /// <para>
    /// The matched years are compared as a <b>set</b>, not as a sequence. Its ancestor in the SQLite-only suite
    /// sends <c>order=year</c> and asserts a sequence, which makes an ordering claim load-bearing inside a
    /// filtering fact — and with two matched rows that claim is a coin flip if the sort is ever dropped, so the
    /// fact would pass half the time for the wrong reason. Ordering is
    /// <see cref="An_order_parameter_orders_the_page_and_the_direction_is_honoured"/>'s job.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_numeric_filter_compares_numerically_rather_than_lexically()
    {
        await using var world = await StartAsync();
        await SeedVehiclesAsync(world);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?year=gt.500", _admin);

        var years = (await response.ReadItemsAsync()).Select(row => row["year"]!.GetValue<int>()).Order().ToArray();
        years.ShouldBe(
            [1999, 2020], "as text every seeded year sorts below '500', so a lexical comparison returns none");
    }

    /// <summary>
    /// The order a request asks for is the order the page arrives in, both ways round.
    /// </summary>
    /// <remarks>
    /// Both directions, because a descending order that was silently dropped still produces a sorted page —
    /// the ascending one — and only the pair fails then. The values are lowercase ASCII on purpose: this fact
    /// is about the direction being honoured, not about the two engines' default collations, which is a
    /// separate claim the port-level ordering suite already owns per engine.
    /// </remarks>
    [Fact]
    public async Task An_order_parameter_orders_the_page_and_the_direction_is_honoured()
    {
        await using var world = await StartAsync();
        await SeedVehiclesAsync(world);

        using var ascending = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make", _admin);
        using var descending = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make.desc", _admin);

        (await ascending.ReadFieldAsync("make")).ShouldBe(["audi", "skoda", "vw"]);
        (await descending.ReadFieldAsync("make")).ShouldBe(["vw", "skoda", "audi"]);
    }

    /// <summary>
    /// Walking the whole set by the cursor the API itself issued visits every row exactly once and stops
    /// after the full last page.
    /// </summary>
    /// <remarks>
    /// Six rows at a page size of three is the one row-count/limit pair that discriminates the mandated
    /// <c>Limit + 1</c> over-fetch from the forbidden <c>Items.Count == Limit</c> derivation: the second page
    /// is simultaneously <b>full</b> and <b>last</b>, so the forbidden form mints a cursor and the walk takes a
    /// third, empty round trip. The page count is therefore asserted as well as the row count — the rows alone
    /// do not tell the two apart.
    /// </remarks>
    [Fact]
    public async Task Walking_the_whole_set_by_cursor_visits_every_row_once_and_stops_on_the_full_last_page()
    {
        await using var world = await StartAsync();
        foreach (var index in Enumerable.Range(0, 6))
        {
            await CreateOwnerAsync(world, Owner($"Owner {index:D2}"));
        }

        var (seen, pages) = await WalkAsync(world, "/api/owners?order=name&limit=3");

        seen.Count.ShouldBe(6);
        seen.Distinct().Count().ShouldBe(6, "a cursor must not revisit a row it already returned");
        pages.ShouldBe(
            2,
            "the forbidden 'Items.Count == Limit' derivation would mint a cursor on the full last page, adding "
            + "a third, empty page to the walk");
    }

    /// <summary>
    /// A read of an audited row hands out a <b>strong</b> entity tag denoting the row's stored version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fact PR4 was told to re-run against a PostgreSQL-backed world, and the reason is
    /// <c>RowVersionETag.For</c>: it mints a tag only when the stored <c>updated_at</c> materializes as a
    /// <see cref="DateTimeOffset"/>. A driver that handed back a <see cref="DateTime"/> would emit <b>no
    /// ETag at all</b>, on every audited entity, with nothing anywhere raising — optimistic concurrency
    /// switched off for that engine and no failing test to say so.
    /// </para>
    /// <para>
    /// The row is written <b>twice</b> before it is read, and the fact says so out loud: on a freshly created
    /// row <c>created_at</c> and <c>updated_at</c> are the same instant, so a tag minted from the wrong audit
    /// column would satisfy this fact exactly as well as the right one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_get_of_an_audited_entity_carries_a_strong_etag()
    {
        await using var world = await StartAsync();
        var id = await CreateOwnerAsync(world, Owner("Ada Ltd"));
        await AdvanceAsync(world, id, "Ada Ltd, renamed");

        using var response = await world.SendAsync(HttpMethod.Get, $"/api/owners/{id}", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        var body = await response.ReadJsonObjectAsync();
        body["created_at"]!.GetValue<DateTimeOffset>().ShouldNotBe(
            body["updated_at"]!.GetValue<DateTimeOffset>(),
            "the two audit instants must differ, or a tag over the wrong one passes this fact too");
        response.Headers.ETag!.IsWeak.ShouldBeFalse("a weak tag can never satisfy a strong If-Match comparison");
        response.ETagOf().ShouldBe(
            TagOf(body["updated_at"]!.GetValue<DateTimeOffset>()),
            "the tag must denote the row's stored version, not the bytes of this representation");
    }

    /// <summary>
    /// The tag this API mints is a tag it accepts: the exact string a read handed out satisfies the
    /// <c>If-Match</c> of the write that follows.
    /// </summary>
    /// <remarks>
    /// Nothing is reconstructed — the string that went out is the string that comes back — because the failure
    /// this guards against is a tag that does not survive its own round trip through the engine's storage.
    /// That failure answers 412 to every conditional write a caller ever sends, which reads exactly like a
    /// genuine concurrent write and says nothing about why. Paired with
    /// <see cref="A_get_of_an_audited_entity_carries_a_strong_etag"/> deliberately: this fact says the tag
    /// round-trips, that one says the tag exists and denotes the stored version.
    /// </remarks>
    [Fact]
    public async Task An_etag_from_a_get_is_accepted_verbatim_by_a_following_update()
    {
        await using var world = await StartAsync();
        var id = await CreateOwnerAsync(world, Owner("Round trip"));

        using var read = await world.SendAsync(HttpMethod.Get, $"/api/owners/{id}", _admin);
        var verbatim = read.ETagOf();
        using var written = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{id}", _admin,
            body: new JsonObject { ["name"] = "Round tripped" }, headers: IfMatch(verbatim));

        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        written.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"the tag '{verbatim}' this API minted must be one it accepts: {await written.ReadTextAsync()}");
        (await ReadOwnerAsync(world, id))["name"]!.GetValue<string>().ShouldBe("Round tripped");
    }

    /// <summary>
    /// The fact §2.1 actually asks for: two callers read the same row, both write with the tag they read, and
    /// the second is refused — so the first caller's change is not silently overwritten.
    /// </summary>
    /// <remarks>
    /// Every other precondition fact can pass while the mechanism is present and inert. This one fails if the
    /// precondition is dropped anywhere along the path — the header, the parse, the port argument, or the
    /// in-transaction comparison — and on this engine that path includes the engine's own equality over a
    /// stored timestamp. The row's final value is asserted as well as the status, because "the second call was
    /// refused" and "the first caller's change survived" are two claims and only the second is the lost update.
    /// </remarks>
    [Fact]
    public async Task A_lost_update_is_prevented_when_two_callers_read_then_both_write()
    {
        await using var world = await StartAsync(_admin, _other);
        var id = await CreateOwnerAsync(world, Owner("Contested"));
        var mine = await ETagOfAsync(world, id, _admin);
        var yours = await ETagOfAsync(world, id, _other);
        mine.ShouldBe(yours, "both callers read the same row version, or they are not racing at all");

        using var first = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{id}", _admin, body: new JsonObject { ["name"] = "Mine" },
            headers: IfMatch(mine));
        using var second = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{id}", _other, body: new JsonObject { ["name"] = "Yours" },
            headers: IfMatch(yours));

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.ReadTextAsync());
        second.StatusCode.ShouldBe(
            HttpStatusCode.PreconditionFailed, "the second writer read a version the row no longer has");
        (await ReadOwnerAsync(world, id))["name"]!.GetValue<string>().ShouldBe("Mine", "the first caller's change must survive");
    }

    /// <summary>
    /// A non-audited entity has no version column, so its responses carry no <c>ETag</c> at all rather than a
    /// tag no write could compare.
    /// </summary>
    /// <remarks>
    /// The negative half of the pair above, and it is what stops "no tag anywhere" from being a green suite:
    /// with only the positive facts, an engine that emitted no tag would fail them, but with only this one an
    /// engine that emitted a tag for everything would pass. Asserted on the raw header, because
    /// <c>Headers.ETag</c> is also <see langword="null"/> for a header that is present and unparsable.
    /// </remarks>
    [Fact]
    public async Task A_get_of_a_non_audited_entity_carries_no_etag_at_all()
    {
        await using var world = await StartAsync();
        var inspection = await CreateInspectionAsync(world);

        using var response = await world.SendAsync(HttpMethod.Get, $"/api/inspections/{inspection}", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        response.Headers.Contains("ETag").ShouldBeFalse(
            "an entity with no version column must advertise no tag, rather than one it cannot compare");
    }

    /// <summary>
    /// A retried create under one <c>Idempotency-Key</c> is answered with the first create's row, and writes
    /// no second one.
    /// </summary>
    /// <remarks>
    /// The engine leg of this is the ledger itself: the record's key is a composite primary key in a table the
    /// framework creates (<c>IdempotencyTable</c>), so its DDL, its uniqueness and its byte-length bound are
    /// per-engine facts. The row count is read off the table, because two responses that look alike cannot
    /// tell a replay from a duplicate row.
    /// </remarks>
    [Fact]
    public async Task A_replayed_create_returns_the_first_row_and_writes_no_second_one()
    {
        await using var world = await StartAsync();

        // No unique field in the body, deliberately. With `email` set, a build whose ledger never reached the
        // port failed this fact on a UNIQUE violation from the second insert — a failure, but the wrong
        // diagnosis: what must fail is "the replay answered with a different row" and "a second row landed".
        var body = Owner("Retry Ltd");

        using var first = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: body, headers: Key("k-1"));
        using var retried = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: body, headers: Key("k-1"));

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.ReadTextAsync());
        retried.StatusCode.ShouldBe(HttpStatusCode.Created, await retried.ReadTextAsync());
        (await IdOfAsync(retried)).ShouldBe(await IdOfAsync(first), "a replay must answer with the first row");
        retried.ETagOf().ShouldBe(first.ETagOf(), "and with the version the first create stored");
        (await world.CountRowsAsync("owners")).ShouldBe(1, "one key, one row — whatever the second response said");
    }

    /// <summary>
    /// One key reused for a <em>different</em> body is a 409, and writes nothing.
    /// </summary>
    /// <remarks>
    /// The dangerous direction is not the 409 but the replay: a ledger that matched too coarsely would answer
    /// the second, different request with the first request's row, and the caller would hold an id for a row
    /// that does not contain what they sent. The row count is asserted too, because a 409 raised <em>after</em>
    /// the insert would be a conflict report over a row that had already landed.
    /// </remarks>
    [Fact]
    public async Task The_same_key_with_a_different_body_is_a_409_that_writes_nothing()
    {
        await using var world = await StartAsync();

        using var first = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: Owner("First Ltd"), headers: Key("k-2"));
        using var second = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: Owner("Second Ltd"), headers: Key("k-2"));

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.ReadTextAsync());
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, await second.ReadTextAsync());
        (await second.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        (await world.CountRowsAsync("owners")).ShouldBe(1, "a conflict must not have written the second row");
    }

    /// <summary>A world on this suite's engine, issuing <paramref name="keys"/> (the one admin by default).</summary>
    /// <param name="keys">The dev API keys the world issues.</param>
    private Task<AlvoApiWorld> StartAsync(params TestApiKey[] keys) =>
        AlvoApiWorld.VehicleRegistryAsync(keys.Length == 0 ? [_admin] : keys, engine: Engine);

    /// <summary>
    /// Follows the cursor from <paramref name="firstPage"/> to exhaustion, collecting every id and counting
    /// the round trips it took.
    /// </summary>
    /// <param name="world">The world to page.</param>
    /// <param name="firstPage">The first page's request path, including its <c>limit</c>.</param>
    private static async Task<(IReadOnlyList<Guid> Seen, int Pages)> WalkAsync(AlvoApiWorld world, string firstPage)
    {
        var seen = new List<Guid>();
        var pages = 0;
        string? cursor = null;

        do
        {
            pages++;
            var path = cursor is null ? firstPage : $"{firstPage}&after={Uri.EscapeDataString(cursor)}";
            using var response = await world.SendAsync(HttpMethod.Get, path, _admin);
            seen.AddRange((await response.ReadItemsAsync()).Select(row => row["id"]!.GetValue<Guid>()));
            cursor = (await response.ReadJsonObjectAsync())["next"]?.GetValue<string>();
        }
        while (cursor is not null);

        return (seen, pages);
    }

    /// <summary>Three vehicles with three makes and three years, plus the owner their required ref needs.</summary>
    private static async Task SeedVehiclesAsync(AlvoApiWorld world)
    {
        var owner = await CreateOwnerAsync(world, Owner("Fleet Ltd"));
        var makes = new[] { ("vw", 2020), ("audi", 1999), ("skoda", 400) };
        foreach (var (index, (make, year)) in makes.Index())
        {
            await CreateVehicleAsync(world, owner, make, year, index);
        }
    }

    private static async Task<Guid> CreateVehicleAsync(
        AlvoApiWorld world, Guid owner, string make, int year, int index) =>
        await CreateAsync(world, "vehicles", new JsonObject
        {
            ["vin"] = $"VIN{index:D14}",
            ["plate"] = $"AA-{index:D3}",
            ["make"] = make,
            ["model"] = "model",
            ["year"] = year,
            ["owner_id"] = owner.ToString(),
        });

    /// <summary>One inspection — the descriptor's only non-audited entity — plus the rows its required refs need.</summary>
    private static async Task<Guid> CreateInspectionAsync(AlvoApiWorld world)
    {
        var owner = await CreateOwnerAsync(world, Owner("Inspecting Ltd"));
        var vehicle = await CreateVehicleAsync(world, owner, "skoda", 2020, 0);

        return await CreateAsync(world, "inspections", new JsonObject
        {
            ["vehicle_id"] = vehicle.ToString(),
            ["inspector_name"] = "Ivan Inspector",
            ["inspected_on"] = "2026-01-15",
        });
    }

    private static Task<Guid> CreateOwnerAsync(AlvoApiWorld world, JsonObject body) =>
        CreateAsync(world, "owners", body);

    private static async Task<Guid> CreateAsync(AlvoApiWorld world, string entity, JsonObject body)
    {
        using var response = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", _admin, body: body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return await IdOfAsync(response);
    }

    /// <summary>Writes the row once <em>without</em> a precondition, so the version a fact held goes stale.</summary>
    private static async Task AdvanceAsync(AlvoApiWorld world, Guid id, string name)
    {
        using var response = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{id}", _admin, body: new JsonObject { ["name"] = name });
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, "the row must really be rewritten, or the two audit instants are still equal");
    }

    private static async Task<JsonObject> ReadOwnerAsync(AlvoApiWorld world, Guid id)
    {
        using var response = await world.SendAsync(HttpMethod.Get, $"/api/owners/{id}", _admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return await response.ReadJsonObjectAsync();
    }

    private static async Task<string> ETagOfAsync(AlvoApiWorld world, Guid id, TestApiKey key)
    {
        using var response = await world.SendAsync(HttpMethod.Get, $"/api/owners/{id}", key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return response.ETagOf();
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response) =>
        (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

    /// <summary>An owner payload; the optional members are omitted rather than sent null when not given.</summary>
    private static JsonObject Owner(string name, string? email = null, string? phone = null)
    {
        var body = new JsonObject { ["name"] = name };
        if (email is not null)
        {
            body["email"] = email;
        }

        if (phone is not null)
        {
            body["phone"] = phone;
        }

        return body;
    }

    /// <summary>
    /// The tag this API must mint for one stored instant, spelled out here rather than taken from the
    /// production encoder.
    /// </summary>
    /// <remarks>
    /// Reusing <c>RowVersionETag</c> would make the assertion agree with itself: an encoder that dropped to
    /// whole seconds would satisfy a comparison against its own output. This is the second, independent
    /// statement of the encoding — quoted invariant <see cref="DateTimeOffset.UtcTicks"/>.
    /// </remarks>
    private static string TagOf(DateTimeOffset version) =>
        $"\"{version.UtcTicks.ToString(CultureInfo.InvariantCulture)}\"";

    private static KeyValuePair<string, string>[] IfMatch(string value) => [new("If-Match", value)];

    private static KeyValuePair<string, string>[] Key(string value) => [new("Idempotency-Key", value)];
}
