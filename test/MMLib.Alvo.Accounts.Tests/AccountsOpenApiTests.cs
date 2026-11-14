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

    private static readonly string[] _tenantPaths =
    [
        "/auth/v1/tenants",
        "/auth/v1/tenants/{tenantId}",
        "/auth/v1/tenants/{tenantId}/members",
        "/auth/v1/tenants/{tenantId}/members/{userId}",
        "/auth/v1/tenants/{tenantId}/invitations",
        "/auth/v1/tenants/{tenantId}/invitations/{invitationId}",
    ];

    private static readonly string[] _invitationPaths =
    [
        "/auth/v1/invitations/preview",
        "/auth/v1/invitations/accept",
    ];

    private static readonly string[] _tenantOperations =
    [
        "tenants.list",
        "tenants.create",
        "tenants.rename",
        "tenants.listMembers",
        "tenants.updateMember",
        "tenants.removeMember",
        "tenants.createInvitation",
        "tenants.listInvitations",
        "tenants.revokeInvitation",
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
        paths.Where(path => path.Contains("auth", StringComparison.Ordinal)
                && !path.Contains("/tenants", StringComparison.Ordinal)
                && !path.Contains("/invitations", StringComparison.Ordinal))
            .ShouldBe(_authPaths, ignoreOrder: true);
        var operations = Operations(document).Where(operation => Tags(operation).Contains("auth")).ToList();
        operations.Select(operation => operation["operationId"]!.GetValue<string>()).ShouldBe(
            ["auth.token", "auth.getUser", "auth.updateUser", "auth.logout", "auth.metadata", "auth.jwks"], ignoreOrder: true);
        operations.ShouldAllBe(operation => operation["summary"] != null && Tags(operation).Count == 1);
    }

    /// <summary>
    /// The self-service routes are mapped whatever the descriptor says (S3 plan D1) — this world's has no
    /// <c>selfService</c> — and each is described once, tagged <c>tenants</c>, taking only the Bearer scheme.
    /// </summary>
    [Fact]
    public async Task Every_tenant_route_is_in_the_document_once()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, openApi: true);

        var document = await world.Api.OpenApiDocumentAsync();

        var paths = document["paths"]!.AsObject().Select(path => path.Key).ToList();
        paths.Where(path => path.StartsWith("/auth/v1/tenants", StringComparison.Ordinal)).ShouldBe(_tenantPaths, ignoreOrder: true);
        var operations = Operations(document).Where(operation => Tags(operation).Contains("tenants")).ToList();
        operations.Select(operation => operation["operationId"]!.GetValue<string>()).ShouldBe(_tenantOperations, ignoreOrder: true);
        operations.ShouldAllBe(operation => operation["summary"] != null && Tags(operation).Count == 1);
        operations.ShouldAllBe(operation => Security(operation) == "[{\"alvoBearer\":[]}]");
        operations.ShouldAllBe(operation => operation["responses"]!["404"] != null);
    }

    /// <summary>
    /// The two routes that take a code (S4 plan D8): tagged <c>invitations</c>, described once each; preview declares no
    /// credential (anyone holding the code), accept only the Bearer scheme.
    /// </summary>
    [Fact]
    public async Task Every_invitation_route_is_in_the_document_once()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, openApi: true);

        var document = await world.Api.OpenApiDocumentAsync();

        var paths = document["paths"]!.AsObject().Select(path => path.Key).ToList();
        paths.Where(path => path.StartsWith("/auth/v1/invitations", StringComparison.Ordinal)).ShouldBe(_invitationPaths, ignoreOrder: true);
        var operations = Operations(document).Where(operation => Tags(operation).Contains("invitations")).ToList();
        operations.Select(operation => operation["operationId"]!.GetValue<string>())
            .ShouldBe(["invitations.preview", "invitations.accept"], ignoreOrder: true);
        operations.ShouldAllBe(operation => operation["summary"] != null && Tags(operation).Count == 1);
        Operation(document, "invitations.preview")["security"].ShouldBeNull();
        Security(Operation(document, "invitations.accept")).ShouldBe("[{\"alvoBearer\":[]}]");
        operations.ShouldAllBe(operation => operation["responses"]!["404"] != null && operation["responses"]!["429"] != null);
    }

    /// <summary>
    /// Spec §6.3: a code is never in a URL — access logs, browser history and <c>Referer</c> carry URLs. No invitation
    /// operation takes a query parameter, preview and accept take no parameter at all, the owner's three take only the ids
    /// in their path; and of the sign-in components only the request body preview and accept read and
    /// <c>tenants.createInvitation</c>'s 201 have a property named <c>code</c> (the core's <c>problemViolation.code</c> is a
    /// violation's stable code, not an invitation's).
    /// </summary>
    [Fact]
    public async Task No_invitation_route_takes_the_code_in_its_url()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, openApi: true);

        var document = await world.Api.OpenApiDocumentAsync();

        var invitations = Operations(document)
            .Where(operation => operation["operationId"]!.GetValue<string>().Contains("nvitation", StringComparison.Ordinal))
            .ToList();
        invitations.Count.ShouldBe(5);
        foreach (var operation in invitations)
        {
            var parameters = operation["parameters"]?.AsArray().Select(parameter => parameter!.AsObject()).ToList() ?? [];
            parameters.ShouldAllBe(parameter => parameter["in"]!.GetValue<string>() == "path");
            parameters.Select(parameter => parameter["name"]!.GetValue<string>()).ShouldBeSubsetOf(["tenantId", "invitationId"]);
        }

        Operation(document, "invitations.preview")["parameters"].ShouldBeNull();
        Operation(document, "invitations.accept")["parameters"].ShouldBeNull();
        var withCode = document["components"]!["schemas"]!.AsObject()
            .Where(schema => schema.Key.StartsWith("auth", StringComparison.Ordinal) && schema.Value!["properties"]?["code"] != null)
            .Select(schema => schema.Key);
        withCode.ShouldBe(["authInvitationCode", "authNewInvitation"], ignoreOrder: true);
        Reference(Operation(document, "invitations.preview")["requestBody"]).ShouldBe("#/components/schemas/authInvitationCode");
        Reference(Operation(document, "invitations.accept")["requestBody"]).ShouldBe("#/components/schemas/authInvitationCode");
        Reference(Operation(document, "tenants.createInvitation")["responses"]!["201"]).ShouldBe("#/components/schemas/authNewInvitation");
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

    private static JsonObject Operation(JsonObject document, string operationId) =>
        Operations(document).Single(operation => operation["operationId"]!.GetValue<string>() == operationId);

    private static string Reference(JsonNode? bodyOrResponse) =>
        bodyOrResponse!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>();

    private static List<string> Tags(JsonObject operation)
        => [.. operation["tags"]?.AsArray().Select(tag => tag!.GetValue<string>()) ?? []];

    private static IEnumerable<JsonObject> Operations(JsonObject document) =>
        document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject()).Select(operation => operation.Value!.AsObject());
}
