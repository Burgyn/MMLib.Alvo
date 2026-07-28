using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The query surface end to end: that the parse is actually <b>wired into</b> the list endpoint, that a refusal
/// is a 422 carrying its violations, and that the filter really narrows the rows a real store returns.
/// </summary>
/// <remarks>
/// <c>QueryStringParserTests</c> proves the grammar; none of it is worth anything if <c>MapList</c> still hands
/// the port an unfiltered query. Every fact here is written so that deleting the parse call — or dropping the
/// projection, or the page size — fails it.
/// </remarks>
public sealed class DataApiQueryTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    [Fact]
    public async Task A_filter_narrows_the_rows_the_store_returns()
    {
        await using var world = await SeededAsync();

        using var filtered = await world.SendAsync(HttpMethod.Get, "/api/vehicles?make=eq.vw", _admin);
        using var everything = await world.SendAsync(HttpMethod.Get, "/api/vehicles", _admin);

        (await filtered.ReadFieldAsync("make")).ShouldBe(["vw"]);
        (await everything.ReadFieldAsync("make")).Count.ShouldBe(3, "or the filtered read proves nothing");
    }

    [Fact]
    public async Task A_group_filter_narrows_to_the_union_of_its_terms()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/vehicles?or=(make.eq.vw,make.eq.audi)&order=make", _admin);

        (await response.ReadFieldAsync("make")).ShouldBe(["audi", "vw"]);
    }

    [Fact]
    public async Task A_negated_filter_excludes_what_the_term_would_have_matched()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/vehicles?not.make=eq.vw&order=make", _admin);

        (await response.ReadFieldAsync("make")).ShouldBe(["audi", "skoda"]);
    }

    [Fact]
    public async Task An_order_parameter_orders_the_page_and_the_direction_is_honoured()
    {
        await using var world = await SeededAsync();

        using var ascending = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make", _admin);
        using var descending = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make.desc", _admin);

        (await ascending.ReadFieldAsync("make")).ShouldBe(["audi", "skoda", "vw"]);
        (await descending.ReadFieldAsync("make")).ShouldBe(["vw", "skoda", "audi"]);
    }

    /// <summary>
    /// A projection keeps exactly the named fields. The negative half is what makes it a projection rather than
    /// a hint: a row must not carry <c>vin</c> merely because the store returned it.
    /// </summary>
    [Fact]
    public async Task A_select_parameter_projects_only_the_named_fields()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?select=make,year", _admin);

        var rows = await response.ReadItemsAsync();
        rows.Count.ShouldBe(3);
        foreach (var row in rows)
        {
            row.Select(pair => pair.Key).ShouldBe(["make", "year"]);
        }
    }

    /// <summary>
    /// A limit is honoured and a cursor continues from it, so paging over HTTP is the keyset paging the port
    /// implements rather than a page the API sliced for itself.
    /// </summary>
    [Fact]
    public async Task A_limit_is_honoured_and_the_cursor_reads_the_next_page()
    {
        await using var world = await SeededAsync();

        using var first = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make&limit=2", _admin);
        var body = await first.ReadJsonObjectAsync();
        var cursor = body["next"]!.GetValue<string>();

        using var second = await world.SendAsync(
            HttpMethod.Get, $"/api/vehicles?order=make&limit=2&after={Uri.EscapeDataString(cursor)}", _admin);

        (await first.ReadFieldAsync("make")).ShouldBe(["audi", "skoda"]);
        (await second.ReadFieldAsync("make")).ShouldBe(["vw"]);
    }

    /// <summary>
    /// The configured maximum is enforced by the endpoint, not merely by the parser in isolation, and it is
    /// asserted at the boundary: a fact at 100 000 would pass against an endpoint whose ceiling was 1 000.
    /// </summary>
    [Fact]
    public async Task A_page_size_past_the_configured_maximum_is_refused_by_the_endpoint()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(ConfigureApi: options =>
            {
                options.MaxPageSize = 5;
                options.DefaultPageSize = 5;
            }));

        using var atMaximum = await world.SendAsync(HttpMethod.Get, "/api/vehicles?limit=5", _admin);
        using var past = await world.SendAsync(HttpMethod.Get, "/api/vehicles?limit=6", _admin);

        atMaximum.StatusCode.ShouldBe(HttpStatusCode.OK);
        past.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await past.ReadProblemDetailAsync()).ShouldContain("page size");
    }

    /// <summary>
    /// The default page size is applied to a request that names none — which is also why a nullable sort key is
    /// unusable over HTTP, since the port refuses a paged read sorted by one.
    /// </summary>
    [Fact]
    public async Task A_request_naming_no_page_size_gets_the_configured_default()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(ConfigureApi: options => options.DefaultPageSize = 2));
        await SeedAsync(world);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make", _admin);

        (await response.ReadFieldAsync("make")).ShouldBe(["audi", "skoda"]);
    }

    /// <summary>
    /// A refusal is a 422 carrying the machine-readable violations §0 principle 4 asks for — a code and a fix
    /// per problem, so an agent can repair the request instead of guessing.
    /// </summary>
    [Fact]
    public async Task A_refused_query_string_is_a_422_carrying_its_violations()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?limit=0", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var violations = (await response.ReadJsonObjectAsync())["violations"]!.AsArray();
        var only = violations.ShouldHaveSingleItem()!.AsObject();
        only["pointer"]!.GetValue<string>().ShouldBe("limit");
        only["code"]!.GetValue<string>().ShouldBe("invalid-page-size");
        only["fixSuggestion"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// An unrecognized parameter is refused rather than ignored: an ignored <c>oder=name</c> answers 200 with
    /// unsorted data and the agent that sent it has no way to notice.
    /// </summary>
    [Fact]
    public async Task An_unrecognized_query_parameter_is_refused_rather_than_ignored()
    {
        await using var world = await SeededAsync();

        using var mistyped = await world.SendAsync(HttpMethod.Get, "/api/vehicles?oder=make", _admin);
        using var correct = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make", _admin);

        mistyped.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        correct.StatusCode.ShouldBe(HttpStatusCode.OK, "or the refusal above could be a blanket refusal of every list");
    }

    /// <summary>
    /// A query string is parsed <b>before</b> any row is read, so a malformed one costs the store nothing.
    /// </summary>
    [Fact]
    public async Task A_refused_query_string_reaches_no_statement()
    {
        await using var world = await SeededAsync();
        world.ClearStatements();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?nosuchfield=eq.1", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        world.Statements.ShouldBeEmpty();
    }

    /// <summary>
    /// A descriptor whose field shadows a reserved query parameter fails at <b>mapping</b>, naming the entity,
    /// the field and the fix — never per request, which would make a descriptor problem look like a caller's.
    /// This is the wiring half of <c>QueryStringParserTests</c>' unit fact: without the call in
    /// <c>DataApiEndpoints.Map</c>, this descriptor would map routes that silently cannot filter by <c>limit</c>.
    /// </summary>
    [Fact]
    public async Task An_entity_shadowing_a_reserved_query_parameter_fails_at_mapping()
    {
        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => AlvoApiWorld.FromDescriptorAsync("reserved-field.alvo.json"));

        refusal.Message.ShouldContain("widgets");
        refusal.Message.ShouldContain("limit");
    }

    private static async Task<AlvoApiWorld> SeededAsync()
    {
        var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        await SeedAsync(world);
        return world;
    }

    /// <summary>Three vehicles behind one owner, created through the API so every row is one the port wrote.</summary>
    private static async Task SeedAsync(AlvoApiWorld world)
    {
        var owner = await CreateAsync(world, "owners", new JsonObject { ["name"] = "Acme Ltd" });

        foreach (var make in new[] { "skoda", "vw", "audi" })
        {
            await CreateAsync(world, "vehicles", new JsonObject
            {
                ["vin"] = $"VIN-{make}",
                ["plate"] = $"PLATE-{make}",
                ["make"] = make,
                ["model"] = "model",
                ["year"] = 2020,
                ["owner_id"] = owner,
            });
        }
    }

    private static async Task<string> CreateAsync(AlvoApiWorld world, string entity, JsonObject body)
    {
        using var created = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", _admin, body: body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>().ToString();
    }
}
