using System.Net;

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
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([reader], routePrefix: "/data");

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
        body.ShouldNotBeNull();
        body!.ContainsKey("items").ShouldBeTrue();
        body.ContainsKey("next").ShouldBeTrue();
        response.Headers.Contains("Link").ShouldBeFalse();
        response.Content.Headers.Contains("Content-Range").ShouldBeFalse();
    }
}
