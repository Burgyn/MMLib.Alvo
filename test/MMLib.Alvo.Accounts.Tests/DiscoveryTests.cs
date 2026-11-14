using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// Discovery (spec §5.4, S2 D7): RFC 8414 metadata at the prefix and at the path-inserted URL, and the JWKS an access
/// token validates against — public halves only, cacheable for five minutes.
/// </summary>
public sealed class DiscoveryTests
{
    private const string Metadata = "/auth/v1/.well-known/oauth-authorization-server";

    private const string InsertedMetadata = "/.well-known/oauth-authorization-server/auth/v1";

    private const string Jwks = "/auth/v1/.well-known/jwks.json";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task The_metadata_names_the_configured_issuer_and_its_endpoints()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        using var response = await world.Api.SendAsync(HttpMethod.Get, Metadata);

        var body = await CacheableAsync(response);
        body["issuer"]!.GetValue<string>().ShouldBe(AccountsWorld.Issuer);
        body["token_endpoint"]!.GetValue<string>().ShouldBe(AccountsWorld.Issuer + "/token");
        body["jwks_uri"]!.GetValue<string>().ShouldBe(AccountsWorld.Issuer + "/.well-known/jwks.json");
        Strings(body, "grant_types_supported").ShouldBe(["urn:ietf:params:oauth:grant-type:token-exchange", "refresh_token"]);
        Strings(body, "token_endpoint_auth_methods_supported").ShouldBe(["none"]);
        Strings(body, "response_types_supported").ShouldBeEmpty();
        body.Select(member => member.Key).ShouldBe(
            ["issuer", "token_endpoint", "jwks_uri", "grant_types_supported", "token_endpoint_auth_methods_supported", "response_types_supported"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Both_metadata_urls_answer_the_same_document()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        using var atPrefix = await world.Api.SendAsync(HttpMethod.Get, Metadata);
        using var inserted = await world.Api.SendAsync(HttpMethod.Get, InsertedMetadata);

        (await CacheableAsync(inserted)).ToJsonString().ShouldBe((await CacheableAsync(atPrefix)).ToJsonString());
    }

    [Fact]
    public async Task The_jwks_publishes_the_signing_key_and_no_private_part()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");

        using var response = await world.Api.SendAsync(HttpMethod.Get, Jwks);

        var key = (await CacheableAsync(response))["keys"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        key.Select(member => member.Key).ShouldBe(["kty", "crv", "x", "y", "kid", "use", "alg"], ignoreOrder: true);
        key["kty"]!.GetValue<string>().ShouldBe("EC");
        key["crv"]!.GetValue<string>().ShouldBe("P-256");
        key["use"]!.GetValue<string>().ShouldBe("sig");
        key["alg"]!.GetValue<string>().ShouldBe("ES256");
        key["kid"]!.GetValue<string>().ShouldBe(new JsonWebToken(access).Kid);
    }

    [Fact]
    public async Task A_token_validates_against_the_published_jwks()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");
        using var response = await world.Api.SendAsync(HttpMethod.Get, Jwks);
        var keys = new JsonWebKeySet(await response.ReadTextAsync());

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(access, new TokenValidationParameters
        {
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidIssuer = AccountsWorld.Issuer,
            ValidAudience = "time-tracker",
            ValidTypes = ["at+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidateLifetime = false,
        });

        result.IsValid.ShouldBeTrue(result.Exception?.Message);
        keys.Keys.ShouldHaveSingleItem().HasPrivateKey.ShouldBeFalse();
    }

    [Fact]
    public async Task In_development_without_an_issuer_the_metadata_follows_the_request()
    {
        await using var world = await AccountsWorld.StartAsync(
            _engine,
            configure: options => options.Issuer = null,
            services: services => Environment(services).EnvironmentName = Environments.Development);

        using var response = await world.Api.SendAsync(HttpMethod.Get, "http://api.dev.test" + Metadata);

        var body = await CacheableAsync(response);
        body["issuer"]!.GetValue<string>().ShouldBe("http://api.dev.test/auth/v1");
        body["token_endpoint"]!.GetValue<string>().ShouldBe("http://api.dev.test/auth/v1/token");
    }

    /// <summary>
    /// Review of Tasks 10–11, Important 2: the cacheable discovery answers vary by <c>Origin</c> whatever the request sent —
    /// no <c>Origin</c>, the one configured origin, or a foreign one — so a shared cache never serves a server-side fetch's
    /// answer (no <c>Access-Control-Allow-Origin</c>) to a browser app.
    /// </summary>
    [Fact]
    public async Task With_one_configured_origin_every_discovery_answer_varies_by_origin()
    {
        await using var world = await AccountsWorld.StartAsync(
            _engine, api: options => options.Cors.Origins.Add("https://app.example.test"));

        foreach (var path in new[] { Metadata, InsertedMetadata, Jwks })
        {
            foreach (var origin in new string?[] { null, "https://app.example.test", "https://evil.example.test" })
            {
                using var response = await world.Api.SendAsync(
                    HttpMethod.Get, path, headers: origin is null ? null : [new KeyValuePair<string, string>("Origin", origin)]);

                await CacheableAsync(response);
                response.Headers.Vary.ToString().ShouldBe("Origin", $"{path} from origin '{origin ?? "(none)"}'");
            }
        }
    }

    private static IHostEnvironment Environment(IServiceCollection services)
        => (IHostEnvironment)services.Last(service => service.ServiceType == typeof(IHostEnvironment)).ImplementationInstance!;

    private static async Task<JsonObject> CacheableAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.CacheControl!.ToString().ShouldBe("public, max-age=300");
        return await response.ReadJsonObjectAsync();
    }

    private static string[] Strings(JsonObject body, string member)
        => [.. body[member]!.AsArray().Select(value => value!.GetValue<string>())];
}
