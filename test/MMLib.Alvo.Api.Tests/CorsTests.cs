using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// CORS on the Data API (spec §7.3, S2 D3): applied by Alvo itself, with no <c>UseCors()</c> in the host — none at all
/// without configured origins, the allowed origin's headers on every answer including refusals, a preflight naming the
/// credential headers, and never credentials.
/// </summary>
public sealed class CorsTests
{
    private const string Allowed = "https://app.example.test";

    private const string Owners = "/api/owners";

    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>Authenticates and holds no scope, so every operation is a 403.</summary>
    private static readonly TestApiKey _scopeless = new("scopeless-key", ["authenticated"], []);

    /// <summary>Presented and never issued, so every operation is a 401.</summary>
    private static readonly TestApiKey _ghost = new("ghost-key", ["admin"], ["*:read"]);

    [Fact]
    public async Task Without_origins_no_cors_header_is_ever_sent()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var get = await world.SendAsync(HttpMethod.Get, Owners, _admin, headers: [Origin(Allowed)]);
        using var preflight = await PreflightAsync(world, Allowed);

        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        CorsHeaders(get).ShouldBeEmpty();
        preflight.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        CorsHeaders(preflight).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_allowed_origin_gets_its_headers_on_success_and_on_401_and_403()
    {
        await using var world = await WithOriginsAsync();

        using var ok = await world.SendAsync(HttpMethod.Get, Owners, _admin, headers: [Origin(Allowed)]);
        using var unauthenticated = await world.SendAsync(HttpMethod.Get, Owners, _ghost, headers: [Origin(Allowed)]);
        using var forbidden = await world.SendAsync(HttpMethod.Get, Owners, _scopeless, headers: [Origin(Allowed)]);

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreach (var response in new[] { ok, unauthenticated, forbidden })
        {
            Single(response, "Access-Control-Allow-Origin").ShouldBe(Allowed);
            Single(response, "Access-Control-Expose-Headers").ShouldContain("WWW-Authenticate");
        }
    }

    [Fact]
    public async Task A_preflight_from_an_allowed_origin_names_the_bearer_and_api_key_headers()
    {
        await using var world = await WithOriginsAsync();

        using var preflight = await PreflightAsync(world, Allowed);

        preflight.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Single(preflight, "Access-Control-Allow-Origin").ShouldBe(Allowed);
        Single(preflight, "Access-Control-Allow-Methods").ShouldContain("POST");
        var headers = Single(preflight, "Access-Control-Allow-Headers").Split(',', StringSplitOptions.TrimEntries)
            .Select(header => header.ToUpperInvariant()).ToList();
        headers.ShouldContain("AUTHORIZATION");
        headers.ShouldContain(world.CredentialHeaderName.ToUpperInvariant());
        Single(preflight, "Access-Control-Max-Age").ShouldBe("600");
    }

    [Fact]
    public async Task Another_origin_gets_nothing()
    {
        await using var world = await WithOriginsAsync();

        using var get = await world.SendAsync(HttpMethod.Get, Owners, _admin, headers: [Origin("https://evil.example.test")]);
        using var preflight = await PreflightAsync(world, "https://evil.example.test");

        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        CorsHeaders(get).ShouldBeEmpty();
        CorsHeaders(preflight).ShouldBeEmpty();
    }

    /// <summary>
    /// With origins configured, every answer says it varies by <c>Origin</c> — once, whether the request sent none, an
    /// allowed one or a foreign one, and with one origin configured, where the framework's evaluator would add nothing.
    /// Without origins no answer carries it.
    /// </summary>
    [Fact]
    public async Task With_origins_every_answer_varies_by_origin_once_and_without_none_does()
    {
        await using var world = await WithOriginsAsync();
        await using var closed = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        foreach (var origin in new string?[] { null, Allowed, "https://evil.example.test" })
        {
            using var get = await world.SendAsync(HttpMethod.Get, Owners, _admin, headers: origin is null ? null : [Origin(origin)]);

            get.StatusCode.ShouldBe(HttpStatusCode.OK);
            get.Headers.Vary.ToString().ShouldBe("Origin", $"from origin '{origin ?? "(none)"}'");
        }

        using var preflight = await PreflightAsync(world, Allowed);
        using var unconfigured = await closed.SendAsync(HttpMethod.Get, Owners, _admin, headers: [Origin(Allowed)]);

        preflight.Headers.Vary.ToString().ShouldBe("Origin");
        unconfigured.Headers.Vary.ShouldBeEmpty();
    }

    [Fact]
    public async Task Credentials_are_never_allowed()
    {
        await using var world = await WithOriginsAsync();

        using var get = await world.SendAsync(HttpMethod.Get, Owners, _admin, headers: [Origin(Allowed)]);
        using var preflight = await PreflightAsync(world, Allowed);

        get.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
        preflight.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    private static Task<AlvoApiWorld> WithOriginsAsync() => AlvoApiWorld.VehicleRegistryAsync(
        [_admin, _scopeless],
        new AlvoApiWorldSetup(ConfigureApi: api => api.Cors.Origins.Add(Allowed + "/")));

    private static Task<HttpResponseMessage> PreflightAsync(AlvoApiWorld world, string origin) => world.SendAsync(
        HttpMethod.Options,
        Owners,
        headers:
        [
            Origin(origin),
            new("Access-Control-Request-Method", "POST"),
            new("Access-Control-Request-Headers", $"authorization,content-type,{world.CredentialHeaderName}"),
        ]);

    private static KeyValuePair<string, string> Origin(string origin) => new("Origin", origin);

    private static string Single(HttpResponseMessage response, string header)
        => response.Headers.GetValues(header).ShouldHaveSingleItem();

    private static List<string> CorsHeaders(HttpResponseMessage response)
        => [.. response.Headers.Select(header => header.Key).Where(name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))];
}
