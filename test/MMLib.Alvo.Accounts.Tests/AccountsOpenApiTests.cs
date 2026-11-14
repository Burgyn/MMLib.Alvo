using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The OpenAPI document of a host with sign-in: the auth operations described once each, the Bearer scheme published,
/// and every Data API operation accepting it beside an API key.
/// </summary>
/// <remarks>
/// The snapshot proves stability only (the <c>OpenApiDocumentTests</c> division); it is linted by <c>scripts/lint-api</c>
/// against the same ruleset as the core's.
/// </remarks>
public sealed class AccountsOpenApiTests
{
    private const string Bearer = "alvoBearer";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    private static readonly string[] _authPaths =
    [
        "/auth/v1/token",
        "/auth/v1/user",
        "/auth/v1/logout",
        "/auth/v1/.well-known/oauth-authorization-server",
        "/auth/v1/.well-known/jwks.json",
    ];

    [Fact]
    public async Task The_document_with_sign_in_is_stable()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, openApi: true);

        await Verify(await world.Api.OpenApiTextAsync());
    }

    [Fact]
    public async Task Every_auth_route_is_in_the_document_once()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, openApi: true);

        var document = await world.Api.OpenApiDocumentAsync();

        var paths = document["paths"]!.AsObject().Select(path => path.Key).ToList();
        paths.Where(path => path.Contains("auth", StringComparison.Ordinal)).ShouldBe(_authPaths, ignoreOrder: true);
        var operations = Operations(document).Where(operation => Tags(operation).Contains("auth")).ToList();
        operations.Select(operation => operation["operationId"]!.GetValue<string>()).ShouldBe(
            ["auth.token", "auth.getUser", "auth.updateUser", "auth.logout", "auth.metadata", "auth.jwks"], ignoreOrder: true);
        operations.ShouldAllBe(operation => operation["summary"] != null && Tags(operation).Count == 1);
    }

    [Fact]
    public async Task Data_api_operations_accept_a_bearer_token()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, openApi: true);

        var document = await world.Api.OpenApiDocumentAsync();

        var scheme = document["components"]!["securitySchemes"]![Bearer]!.AsObject();
        scheme["type"]!.GetValue<string>().ShouldBe("http");
        scheme["scheme"]!.GetValue<string>().ShouldBe("bearer");
        scheme["bearerFormat"]!.GetValue<string>().ShouldBe("JWT");
        scheme["description"]!.GetValue<string>().ShouldContain("POST /auth/v1/token");
        var dataApi = document["paths"]!.AsObject().Where(path => path.Key.StartsWith("/api/", StringComparison.Ordinal))
            .SelectMany(path => path.Value!.AsObject()).Select(operation => operation.Value!.AsObject()).ToList();
        dataApi.ShouldNotBeEmpty();
        dataApi.ShouldAllBe(operation => Security(operation) == "[{},{\"alvoApiKey\":[]},{\"alvoBearer\":[]}]");
    }

    private static string Security(JsonObject operation) => operation["security"]!.ToJsonString();

    private static List<string> Tags(JsonObject operation)
        => [.. operation["tags"]?.AsArray().Select(tag => tag!.GetValue<string>()) ?? []];

    private static IEnumerable<JsonObject> Operations(JsonObject document) =>
        document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject()).Select(operation => operation.Value!.AsObject());
}
