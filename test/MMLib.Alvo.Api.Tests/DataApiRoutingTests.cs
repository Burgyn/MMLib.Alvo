using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Rules;
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
    /// asserted too: a seventh route per entity, or a stray catch-all, has to fail something.
    /// </summary>
    [Fact]
    public async Task Every_entity_in_the_applied_schema_gets_six_routes()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync();

        var routes = world.Routes;

        foreach (var entity in _entities)
        {
            routes.ShouldContain($"GET /api/{entity}");
            routes.ShouldContain($"GET /api/{entity}/{{id:guid}}");
            routes.ShouldContain($"POST /api/{entity}");
            routes.ShouldContain($"POST /api/{entity}/query");
            routes.ShouldContain($"PATCH /api/{entity}/{{id:guid}}");
            routes.ShouldContain($"DELETE /api/{entity}/{{id:guid}}");
        }

        routes.Count.ShouldBe(
            _entities.Length * 6,
            $"exactly six routes per declared entity and nothing else: {string.Join(", ", routes)}");
    }

    /// <summary>
    /// A verb the query route does not answer is a 405 from routing itself, not a 404 and not a problem
    /// document: the path exists, and this is the one response on these paths that Alvo does not write.
    /// Asserted so the change from 404 is a recorded behaviour rather than a discovery.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task A_verb_the_query_route_does_not_answer_is_a_405_from_routing(string method)
    {
        var reader = new TestApiKey("reader", ["authenticated"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([reader]);

        using var response = await world.SendRawAsync(new HttpMethod(method), "/api/owners/query", reader);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty(
            "a 405 produced by routing carries no body; one produced by an endpoint would carry a problem "
            + "document");
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

        using var vehicle = await CreateVehicleAsync(world, admin);
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

    /// <summary>Creates an owner and a vehicle referencing it, and returns the vehicle's response.</summary>
    private static async Task<HttpResponseMessage> CreateVehicleAsync(AlvoApiWorld world, TestApiKey admin)
    {
        using var owner = await world.SendAsync(
            HttpMethod.Post, "/api/owners", admin, body: new JsonObject { ["name"] = "Acme Ltd" });
        var ownerId = (await owner.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

        return await world.SendAsync(HttpMethod.Post, "/api/vehicles", admin, body: new JsonObject
        {
            ["vin"] = "VIN01234567890123",
            ["plate"] = "ACME-001",
            ["make"] = "Skoda",
            ["model"] = "Octavia",
            ["year"] = 2020,
            ["owner_id"] = ownerId.ToString(),
        });
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

    /// <summary>
    /// A prefix of nothing but slashes mounts the entities at the root — and this fact <b>serves a
    /// request</b> to prove it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lesson this fact was rewritten to carry: <b>a validator returning success is not evidence that a
    /// value works.</b> Its predecessor asserted only that <c>AlvoApiOptionsValidator</c> accepted
    /// <c>"/"</c>, and passed for a whole round while <c>NormalizePrefix("/")</c> returned <c>"/"</c>,
    /// <c>Map</c> built <c>"//owners"</c>, and <c>RoutePatternFactory.Parse</c> threw on the empty segment.
    /// Nothing mounted. Only mounting proves mounting.
    /// </para>
    /// <para>
    /// All three spellings are exercised, because they reduce through the same trim and a fix that repaired
    /// only <c>"/"</c> would leave the other two throwing.
    /// </para>
    /// </remarks>
    /// <param name="prefix">A configured prefix that carries no path segment at all.</param>
    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData(" / ")]
    public async Task The_route_prefix_can_mount_at_the_root(string prefix)
    {
        var reader = new TestApiKey("reader", ["authenticated"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [reader], new AlvoApiWorldSetup(api => api.RoutePrefix = prefix));

        world.Routes.ShouldContain("GET /owners");

        using var response = await world.SendAsync(HttpMethod.Get, "/owners", reader);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the root-mounted route must actually serve");
        (await response.ReadItemsAsync()).ShouldNotBeNull();
    }

    /// <summary>
    /// Every generated endpoint carries an operation marker, and the operation matches the verb and shape of
    /// its own route. This is the half of the authorization guarantee that <b>scales</b>: the five per-verb
    /// facts prove the gate refuses, and they name five literal paths, so a sixth endpoint added later would
    /// be covered by nothing. This one is written over the endpoint table, so it covers whatever is mapped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It asserts the marker rather than the filter because <c>AddEndpointFilter</c> leaves nothing in
    /// <c>Endpoint.Metadata</c>. What makes the marker sufficient evidence is
    /// <c>DataApiEndpoints.Protect</c>: it attaches both in one call, so no code <em>in this framework</em> can
    /// produce a marker without a filter.
    /// </para>
    /// <para>
    /// <b>Host code is a different matter, and always was.</b> A convention a host attaches to
    /// <c>MapAlvoDataApi()</c> receives the <c>EndpointBuilder</c> and could clear its filter factories — as it
    /// could before that seam existed, through <c>app.MapGroup("").MapAlvoDataApi()</c> and conventions on the
    /// group, and as it could anyway by substituting <c>IPolicyEngine</c> in its own container. The guarantee
    /// this fact carries is therefore about the framework's own construction, not about a host that decides to
    /// dismantle it: an embedded host owns its pipeline, and treating its own code as an attacker is not this
    /// project's threat model.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_generated_endpoint_carries_an_operation_marker_matching_its_verb()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync();

        var endpoints = world.Endpoints;

        endpoints.Count.ShouldBe(_entities.Length * 6, "or this fact is asserting over the wrong set");
        foreach (var endpoint in endpoints)
        {
            var marker = endpoint.Metadata.GetMetadata<DataApiOperationMetadata>();
            marker.ShouldNotBeNull($"{endpoint.RoutePattern.RawText} carries no operation marker, so nothing gates it");
            _entities.ShouldContain(marker.Entity);
            marker.Operation.ShouldBe(ExpectedOperation(endpoint));
        }
    }

    /// <summary>
    /// The kind is the API layer's own vocabulary and the operation is policy's. Two kinds map to
    /// <c>list</c> on purpose — a second, body-shaped way to reach the same read — and every other kind is
    /// one-to-one, so a kind added later cannot silently gate as the wrong operation.
    /// </summary>
    [Fact]
    public void Every_endpoint_kind_maps_to_the_operation_its_filter_must_gate()
    {
        DataApiEndpointKind.List.ToDataOperation().ShouldBe(DataOperation.List);
        DataApiEndpointKind.Query.ToDataOperation().ShouldBe(DataOperation.List);
        DataApiEndpointKind.Get.ToDataOperation().ShouldBe(DataOperation.Get);
        DataApiEndpointKind.Create.ToDataOperation().ShouldBe(DataOperation.Create);
        DataApiEndpointKind.Update.ToDataOperation().ShouldBe(DataOperation.Update);
        DataApiEndpointKind.Delete.ToDataOperation().ShouldBe(DataOperation.Delete);
    }

    /// <summary>
    /// A kind's wire name is what the document's <c>operationId</c> is built from, so the five that existed
    /// before this split must keep the spelling they published — and the sixth must not collide with them.
    /// </summary>
    [Fact]
    public void Every_endpoint_kind_has_its_own_wire_name_and_the_five_original_ones_are_unchanged()
    {
        DataApiEndpointKind.List.ToWireName().ShouldBe("list");
        DataApiEndpointKind.Get.ToWireName().ShouldBe("get");
        DataApiEndpointKind.Create.ToWireName().ShouldBe("create");
        DataApiEndpointKind.Update.ToWireName().ShouldBe("update");
        DataApiEndpointKind.Delete.ToWireName().ShouldBe("delete");
        DataApiEndpointKind.Query.ToWireName().ShouldBe("query");

        var kinds = Enum.GetValues<DataApiEndpointKind>();
        kinds.Select(kind => kind.ToWireName()).Distinct(StringComparer.Ordinal).Count().ShouldBe(kinds.Length);
    }

    /// <summary>
    /// The operation a route's own shape implies, derived from the verb, whether the pattern addresses one
    /// row, and whether it is the body-shaped read — so a marker that says <c>List</c> on a <c>DELETE</c>
    /// fails rather than being taken at its word.
    /// </summary>
    private static DataOperation ExpectedOperation(RouteEndpoint endpoint)
    {
        var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single();
        var pattern = endpoint.RoutePattern.RawText!;
        var addressesOneRow = pattern.EndsWith("{id:guid}", StringComparison.Ordinal);
        var isQueryByBody = pattern.EndsWith("/query", StringComparison.Ordinal);

        return method switch
        {
            "GET" when addressesOneRow => DataOperation.Get,
            "GET" => DataOperation.List,
            "POST" when isQueryByBody => DataOperation.List,
            "POST" => DataOperation.Create,
            "PATCH" => DataOperation.Update,
            "DELETE" => DataOperation.Delete,
            _ => throw new InvalidOperationException($"Unexpected generated route: {method} {pattern}"),
        };
    }
}
