using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// #130: what a client resolves the document's path keys <em>against</em>. The path keys themselves are
/// <see cref="OpenApiDocumentTests.Every_mapped_route_appears_in_the_document_and_nothing_else_does"/>'s;
/// this file owns <c>servers[0].url</c>, which is what makes those keys reachable or wrong by a prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>The origin is pinned whole and then followed, and both halves are needed.</b> In-process a host under a
/// path base answers the unprefixed URL too — <c>UsePathBase</c> strips a prefix when the request carries one
/// rather than requiring one — so "resolve a path key and get 200" passes for a document that advertises
/// <c>http://localhost/</c> while every path in it is wrong by the prefix at the edge. That is #121's own
/// lesson, and <c>PathBaseTests</c> pins its <c>Location</c> whole for the same reason. The follow-up runs
/// anyway, because a URL that resolves nowhere is the failure a client actually meets and a string comparison
/// passes for one.
/// </para>
/// <para>
/// The scheme and host halves of the same value are pinned by
/// <c>MMLib.Alvo.Host.Tests.AlvoHostForwardedOriginTests</c>; the path-base half was pinned by nothing, which
/// is how #130 stood open through a release describing a defect this runtime does not have.
/// </para>
/// </remarks>
public class OpenApiServersTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    private const string PathBase = "/alvo";

    /// <summary>With no path base the origin is the bare root, so the fix below is additive.</summary>
    [Fact]
    public async Task With_no_path_base_the_document_advertises_the_bare_origin()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true));

        var origin = await OriginAsync(world, "/openapi/v1.json");

        origin.ShouldBe("http://localhost/");
    }

    /// <summary>
    /// The shape #130 names: served under <c>app.UsePathBase("/alvo")</c> and fetched under it, the origin
    /// carries the prefix and a path key resolved against it reaches the endpoint.
    /// </summary>
    [Fact]
    public async Task Behind_a_path_base_the_documents_origin_carries_it_and_a_path_key_resolves()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true, PathBase: PathBase));

        var document = await world.OpenApiDocumentAsync($"{PathBase}/openapi/v1.json");
        var origin = Origin(document);
        var resolved = Resolve(origin, CollectionPathKey(document));

        await FollowingItAnswersOkAsync(world, resolved);
        origin.ShouldBe($"http://localhost{PathBase}");
        resolved.ShouldBe($"http://localhost{PathBase}/api/owners");
    }

    /// <summary>
    /// The other supported mount: a route group's prefix belongs to the <em>route</em>, so it is in the path
    /// key and the origin stays bare. Named because the opposite mistake — putting a group prefix into
    /// <c>servers</c> — would double it for every client that resolves.
    /// </summary>
    [Fact]
    public async Task Under_a_route_group_the_prefix_is_in_the_path_key_and_not_in_the_origin()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true, RouteGroupPrefix: "/backend"));

        var document = await world.OpenApiDocumentAsync("/openapi/v1.json");
        var resolved = Resolve(Origin(document), CollectionPathKey(document));

        await FollowingItAnswersOkAsync(world, resolved);
        Origin(document).ShouldBe("http://localhost/");
        resolved.ShouldBe("http://localhost/backend/api/owners");
    }

    private static async Task<string> OriginAsync(AlvoApiWorld world, string path) =>
        Origin(await world.OpenApiDocumentAsync(path));

    /// <summary>The one origin the document advertises, read the way a generated client reads it.</summary>
    private static string Origin(JsonObject document)
    {
        var servers = document["servers"]!.AsArray();

        servers.Count.ShouldBe(
            1, $"the document must advertise exactly one server; it advertised {servers.Count}");

        return servers[0]!["url"]!.GetValue<string>();
    }

    /// <summary>The <c>owners</c> collection path key, taken from the document rather than written here.</summary>
    private static string CollectionPathKey(JsonObject document) =>
        document["paths"]!.AsObject()
            .Select(path => path.Key)
            .First(key => key.EndsWith("/api/owners", StringComparison.Ordinal));

    /// <summary>
    /// One path key resolved against the advertised origin, exactly as RFC 3986 and every generated client do
    /// it — which is what makes a missing prefix in the origin observable rather than a matter of taste.
    /// </summary>
    private static string Resolve(string origin, string pathKey) =>
        new Uri(new Uri(origin.EndsWith('/') ? origin : origin + "/"), pathKey.TrimStart('/')).ToString();

    private static async Task FollowingItAnswersOkAsync(AlvoApiWorld world, string resolved)
    {
        using var response = await world.SendAsync(HttpMethod.Get, resolved, _admin);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a client that resolves a path key against the advertised origin must reach the endpoint; "
                + $"'{resolved}' did not");
    }
}
