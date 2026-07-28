using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// What the generated Data API maps, and — just as load-bearing — what it does not. Every fact here is
/// read off the mapped endpoint set or off a response that routing alone produced, never off the port:
/// "the descriptor decides which paths exist" is a routing claim, and answering it from the store would
/// be the catch-all design this task exists to refuse.
/// </summary>
public sealed class DataApiRoutingTests
{
    private static readonly string[] _entities = ["owners", "vehicles", "inspections"];

    /// <summary>
    /// The whole route table, spelled out rather than derived from the code that builds it. The count is
    /// asserted too: a sixth route per entity, or a stray catch-all, has to fail something.
    /// </summary>
    [Fact]
    public async Task Every_entity_in_the_applied_schema_gets_five_routes()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync();

        var routes = world.Routes;

        foreach (var entity in _entities)
        {
            routes.ShouldContain($"GET /api/{entity}");
            routes.ShouldContain($"GET /api/{entity}/{{id:guid}}");
            routes.ShouldContain($"POST /api/{entity}");
            routes.ShouldContain($"PATCH /api/{entity}/{{id:guid}}");
            routes.ShouldContain($"DELETE /api/{entity}/{{id:guid}}");
        }

        routes.Count.ShouldBe(
            _entities.Length * 5,
            $"exactly five routes per declared entity and nothing else: {string.Join(", ", routes)}");
    }

    /// <summary>
    /// PUT is deliberately absent: <c>UpdateAsync</c> is partial by contract, so a PUT would advertise
    /// whole-resource replacement the port does not perform. Asserted as its own fact because the
    /// route-table fact above would also pass if PUT were mapped <em>instead of</em> PATCH on a future
    /// edit that swapped them.
    /// </summary>
    [Fact]
    public async Task No_entity_gets_a_put_route()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync();

        world.Routes.ShouldNotContain(route => route.StartsWith("PUT ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The refusal of an undeclared entity must come from routing, not from the port. Three assertions
    /// make that distinguishable from a <c>{entity}</c> catch-all, which would also answer 404: no
    /// mapped pattern carries an entity route parameter, the response body is empty (a port-produced
    /// 404 carries a problem document), and no statement reached the database.
    /// </summary>
    [Fact]
    public async Task An_entity_the_descriptor_does_not_declare_has_no_route_at_all()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync();
        world.Routes.ShouldNotContain(route => route.Contains("{entity", StringComparison.Ordinal));
        world.ClearStatements();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/widgets");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty(
            "a 404 produced by routing carries no body; one produced by the port would carry a problem document");
        world.Statements.ShouldBeEmpty("an entity with no route must never reach the store");
    }

    /// <summary>
    /// The prefix is configuration, and nothing leaks outside it: every mapped pattern sits under the
    /// configured prefix, the default prefix answers nothing, and the configured one answers.
    /// </summary>
    [Fact]
    public async Task The_route_prefix_is_configurable_and_nothing_is_mapped_outside_it()
    {
        var reader = new TestApiKey("reader", ["authenticated"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [reader], new AlvoApiWorldSetup(api => api.RoutePrefix = "/data"));

        world.Routes.ShouldAllBe(route => route.Contains(" /data/", StringComparison.Ordinal));

        using var outside = await world.SendAsync(HttpMethod.Get, "/api/owners", reader);
        using var inside = await world.SendAsync(HttpMethod.Get, "/data/owners", reader);

        outside.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        inside.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The list envelope is a JSON object carrying <c>items</c> and <c>next</c> — never a bare array
    /// with the next page in a <c>Content-Range</c> or <c>Link</c> header, so a cursor has exactly one
    /// home and an agent reading the body needs no header parsing.
    /// </summary>
    [Fact]
    public async Task A_list_response_is_an_items_and_next_envelope_with_no_paging_headers()
    {
        var reader = new TestApiKey("reader", ["authenticated"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([reader]);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", reader);

        var body = await response.ReadJsonObjectAsync();
        body.ContainsKey("items").ShouldBeTrue();
        body.ContainsKey("next").ShouldBeTrue();
        response.Headers.Contains("Link").ShouldBeFalse();
        response.Content.Headers.Contains("Content-Range").ShouldBeFalse();
    }

    /// <summary>
    /// A create's <c>Location</c> must name the row that was created. The port guarantees a returned record
    /// carries every framework-managed column, <c>id</c> included, so a header ending in a bare slash is not
    /// a cosmetic slip — it is the invariant having been broken and the create reported as a success anyway.
    /// </summary>
    [Fact]
    public async Task A_create_returns_a_location_header_naming_the_new_row()
    {
        var admin = new TestApiKey("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([admin]);

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/owners", admin, body: new JsonObject { ["name"] = "Acme Ltd" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
        created.Headers.Location!.ToString().ShouldBe($"/api/owners/{id}");
    }

    /// <summary>
    /// A row's keys are the descriptor's own field names, and they are a contract — the names every rule,
    /// filter, scope and OpenAPI schema uses — so a host's serializer settings must not move them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host configures <see cref="JsonNamingPolicy.SnakeCaseUpper"/> as its
    /// <see cref="JsonSerializerOptions.DictionaryKeyPolicy"/> — <b>not</b> camelCase, which is where the
    /// first attempt at this fact went wrong: camelCase only lowercases a leading capital, so it is a
    /// no-op on an already-lower <c>owner_id</c> and the fact passed with the fix reverted. A policy that
    /// really renames (<c>owner_id</c> → <c>OWNER_ID</c>) is what makes the claim testable.
    /// </para>
    /// <para>
    /// Both directions are asserted: the <c>snake_case</c> name comes back verbatim on every response
    /// path, and it is still accepted verbatim on the way in.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_hosts_json_naming_policy_does_not_rename_a_descriptors_fields()
    {
        var admin = new TestApiKey("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([admin], new AlvoApiWorldSetup(
            ConfigureHostJson: json =>
            {
                json.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper;
                json.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseUpper;
            }));

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/owners", admin, body: new JsonObject { ["name"] = "Acme Ltd" });
        var ownerId = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
        using var vehicle = await world.SendAsync(HttpMethod.Post, "/api/vehicles", admin, body: new JsonObject
        {
            ["vin"] = "VIN01234567890123",
            ["plate"] = "ACME-001",
            ["make"] = "Skoda",
            ["model"] = "Octavia",
            ["year"] = 2020,
            ["owner_id"] = ownerId.ToString(),
        });

        vehicle.StatusCode.ShouldBe(
            HttpStatusCode.Created, "the snake_case field name must still be accepted on the way in");
        var vehicleId = (await vehicle.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/vehicles/{vehicleId}", admin);
        using var listed = await world.SendAsync(HttpMethod.Get, "/api/vehicles", admin);

        // All three response paths, because each writes its own result: the create's 201, the single-row
        // read, and the page envelope's rows. Only checking one would leave the others under the host.
        foreach (var body in new[]
                 {
                     await vehicle.ReadJsonObjectAsync(),
                     await read.ReadJsonObjectAsync(),
                     (await listed.ReadItemsAsync())[0],
                 })
        {
            body.ContainsKey("owner_id").ShouldBeTrue("the descriptor's field name must survive the host's policy");
            body.ContainsKey("OWNER_ID").ShouldBeFalse("a host must not be able to rename a field Alvo publishes");
        }
    }

    /// <summary>
    /// The envelope's own two members are pinned the same way and for the same reason: they are what the
    /// OpenAPI document will describe.
    /// </summary>
    [Fact]
    public async Task A_hosts_json_naming_policy_does_not_rename_the_page_envelope()
    {
        var reader = new TestApiKey("reader", ["authenticated"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([reader], new AlvoApiWorldSetup(
            ConfigureHostJson: json => json.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper));

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", reader);

        var body = await response.ReadJsonObjectAsync();
        body.ContainsKey("items").ShouldBeTrue();
        body.ContainsKey("next").ShouldBeTrue();
    }
}
