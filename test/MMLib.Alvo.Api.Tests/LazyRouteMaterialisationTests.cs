using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The ordering obligation this feature removes: <c>apply → map → listen</c> becomes
/// <c>map → boot → listen → first request materialises the routes</c>. Every fact here starts a world that
/// maps the Data API <b>before</b> anything has primed the applied schema, which is what the old eager
/// <c>foreach</c> made impossible.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="The_OpenApi_document_lists_every_mapped_entity_route"/> is not a documentation fact — it is
/// the trap.</b> Endpoints built by hand with a <c>RouteEndpointBuilder</c> route perfectly and are invisible
/// to ApiExplorer, so a suite that only asserted routing would stay entirely green while the OpenAPI document
/// PR3 built silently emptied (measured: design facts 4 and 5). It is the reason
/// <c>AlvoEndpointDataSource</c> is required to build through the real minimal-API <c>Map*</c> helpers.
/// </para>
/// <para>
/// <b>What is deliberately <em>not</em> claimed here:</b> that a schema primed after the server is already
/// listening produces routes. The framework does allow it (design fact 2), but Alvo's boot service primes
/// during <c>StartingAsync</c>, so no production wiring can reach that state — and a fact asserting it would
/// have to fake one. The property that matters, and the one asserted, is that <em>mapping</em> no longer needs
/// a primed schema.
/// </para>
/// </remarks>
public sealed class LazyRouteMaterialisationTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// The entities <c>examples/vehicle-registry</c> declares — pinned here rather than read from the schema
    /// under test, so a document with no paths at all cannot satisfy the fact below.
    /// </summary>
    private static readonly string[] _entities = ["owners", "vehicles", "inspections"];

    /// <summary>
    /// The coupling this task exists to break: the host mapped its routes while the applied schema was still
    /// empty, and the route literals came from the schema the boot primed afterwards.
    /// </summary>
    [Fact]
    public async Task Routes_materialise_from_a_schema_primed_after_the_routes_were_mapped()
    {
        await using var world = await MappedBeforePrimingAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Fail-closed survives the move: laziness must not turn "the descriptor declares no such entity" into a
    /// route that resolves and then refuses. The 404 has to come from routing, so the body is empty and the
    /// store is never reached.
    /// </summary>
    [Fact]
    public async Task An_entity_the_descriptor_does_not_declare_still_has_no_route()
    {
        await using var world = await MappedBeforePrimingAsync();
        world.ClearStatements();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/nope", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty(
            "a 404 produced by routing carries no body; one produced by the port would carry a problem document");
        world.Statements.ShouldBeEmpty("an entity with no route must never reach the store");
    }

    /// <summary>
    /// Every entity route the lazy data source materialises is also in the OpenAPI document — the one claim a
    /// routing-only suite cannot make, and the one a hand-built endpoint silently breaks.
    /// </summary>
    /// <remarks>
    /// The path set is compared against <see cref="_entities"/> and its count is pinned, so neither an empty
    /// document nor a document that lost exactly one entity can pass.
    /// </remarks>
    [Fact]
    public async Task The_OpenApi_document_lists_every_mapped_entity_route()
    {
        await using var world = await MappedBeforePrimingAsync(servesItsDocument: true);

        var paths = DocumentedPaths(await world.OpenApiDocumentAsync());

        foreach (var entity in _entities)
        {
            paths.ShouldContain($"/api/{entity}");
            paths.ShouldContain($"/api/{entity}/{{id}}");
            paths.ShouldContain($"/api/{entity}/query");
        }

        paths.Count.ShouldBe(
            _entities.Length * PathsPerEntity,
            "the document must list a collection, an item and a query path per declared entity: "
            + string.Join(", ", paths));
    }

    /// <summary>
    /// A collection path, an item path, the query path and the batch path, which is what nine routes
    /// collapse to once the verbs sharing a path are folded together.
    /// </summary>
    /// <remarks>
    /// The two arithmetics are deliberately different. The body-shaped read added one path and one route; the
    /// batch added one path and <em>three</em> routes, because its three verbs share one path. A change that
    /// moved these two numbers by the same amount would be describing something else.
    /// </remarks>
    private const int PathsPerEntity = 4;

    /// <summary>
    /// <c>WebApplicationBuilder</c> wires <c>UseRouting</c>/<c>UseEndpoints</c> only when
    /// <c>DataSources.Count &gt; 0</c>, and it counts <b>sources, not endpoints</b> — so an Alvo that mapped
    /// nothing at all while the schema was unknown would leave routing out of the pipeline entirely and no
    /// later priming could put it back.
    /// </summary>
    /// <remarks>
    /// Both halves are asserted: the source is registered, and it is <em>empty</em> over an unprimed registry.
    /// The second half is what makes the first non-vacuous — it proves the source was registered without the
    /// schema, rather than after something quietly primed one.
    /// </remarks>
    [Fact]
    public void MapAlvoDataApi_registers_a_data_source_even_before_the_schema_is_known()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));
        using var app = builder.Build();

        app.MapAlvoDataApi();

        var sources = ((IEndpointRouteBuilder)app).DataSources;
        sources.ShouldNotBeEmpty("routing is never added to the pipeline when no data source is registered");
        sources.SelectMany(source => source.Endpoints).ShouldBeEmpty(
            "an unprimed schema declares no entity, so it can produce no route");
    }

    /// <summary>A world that mapped its routes before its schema existed.</summary>
    /// <param name="servesItsDocument">Whether the world also serves its OpenAPI document.</param>
    private static Task<AlvoApiWorld> MappedBeforePrimingAsync(bool servesItsDocument = false) =>
        AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(MapBeforePriming: true, MapOpenApiDocument: servesItsDocument));

    /// <summary>The paths the document declares, as the document itself spells them.</summary>
    /// <param name="document">The served document.</param>
    private static IReadOnlyList<string> DocumentedPaths(JsonObject document) =>
        [.. document["paths"]!.AsObject().Select(path => path.Key)];
}
