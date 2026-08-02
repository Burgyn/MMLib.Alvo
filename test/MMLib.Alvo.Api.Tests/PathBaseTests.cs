using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// #121: the created row's <c>Location</c> is built from the mapped route template, which does not carry the
/// request's <c>PathBase</c> — so behind a path base a client that follows the header gets a 404.
/// </summary>
/// <remarks>
/// Both facts <b>follow the header with a real request</b> rather than comparing it to a string, because that
/// is what #121's acceptance asks for and because a string comparison passes for a URL that resolves nowhere.
/// The follow-up runs <em>first</em>, so it is the assertion that fails when the header is wrong; the equality
/// after it is the shape check a resolving URL can still get wrong — a header carrying the base <em>twice</em>
/// would 404 and be caught, but one carrying <c>http://localhost/alvo/…</c> would resolve and is not what the
/// route advertises.
/// </remarks>
public class PathBaseTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>The no-path-base leg: the header keeps its current shape, so the fix is additive.</summary>
    [Fact]
    public async Task With_no_path_base_a_created_rows_location_is_the_route_itself()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        var location = await CreateAndReadLocationAsync(world);

        await FollowingItAnswersOkAsync(world, location);
        location.ShouldBe($"/api/owners/{IdIn(location)}");
    }

    /// <summary>
    /// The embedded shape #121 names: <c>app.UsePathBase("/alvo")</c> then <c>app.MapAlvoDataApi()</c>. The row
    /// really lives under the base, so the header has to say so.
    /// </summary>
    [Fact]
    public async Task Behind_a_path_base_a_created_rows_location_resolves()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(PathBase: "/alvo"));

        var location = await CreateAndReadLocationAsync(world, "/alvo/api/owners");

        await FollowingItAnswersOkAsync(world, location);
        location.ShouldBe($"/alvo/api/owners/{IdIn(location)}");
    }

    /// <summary>
    /// A non-ASCII path base: the header is the <em>encoded</em> URI reference, and following it still reaches
    /// the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PathString.Value</c> is the decoded form, and a response header is not the place for one. Over
    /// Kestrel this is a 500 on a create that <b>already committed the row</b> — response header values are
    /// encoded as Latin-1, and <c>ú</c> is not in it — and behind a proxy that sets
    /// <c>X-Forwarded-Prefix: /my%20app</c> it is a header no client can parse as a URI reference.
    /// </para>
    /// <para>
    /// The ASCII assertion is what makes this a fact about encoding rather than about routing:
    /// <c>TestServer</c> has no Latin-1 header writer, so it carries the decoded value happily and the
    /// follow-up alone would pass either way. The equality after it pins the exact encoded shape, so a
    /// double-encoded header — which would also resolve, <c>%25C3%25BA</c> and all — is caught too.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Behind_a_non_ascii_path_base_a_created_rows_location_is_percent_encoded()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(PathBase: "/účty"));

        var location = await CreateAndReadLocationAsync(world, "/účty/api/owners");

        await FollowingItAnswersOkAsync(world, location);
        location.ShouldAllBe(character => char.IsAscii(character), $"'{location}' is not a usable header value");
        location.ShouldBe($"/%C3%BA%C4%8Dty/api/owners/{IdIn(location)}");
    }

    private static async Task<string> CreateAndReadLocationAsync(AlvoApiWorld world, string path = "/api/owners")
    {
        using var response = await world.SendAsync(
            HttpMethod.Post, path, _admin, body: new JsonObject { ["name"] = "Followed Ltd" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return response.Headers.Location!.ToString();
    }

    private static async Task FollowingItAnswersOkAsync(AlvoApiWorld world, string location)
    {
        using var followed = await world.SendAsync(HttpMethod.Get, location, _admin);

        followed.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"a client that follows Location must reach the row; '{location}' did not");
    }

    private static string IdIn(string location) => location[(location.LastIndexOf('/') + 1)..];
}
