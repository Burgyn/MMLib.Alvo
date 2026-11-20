using MMLib.Alvo.DocsGen.Host;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Host;

public class AccountsReferenceTests
{
    private static readonly string[] _signInPaths =
    [
        "/auth/v1/.well-known/jwks.json",
        "/auth/v1/.well-known/oauth-authorization-server",
        "/auth/v1/invitations/accept",
        "/auth/v1/invitations/preview",
        "/auth/v1/logout",
        "/auth/v1/tenants",
        "/auth/v1/tenants/{tenantId}",
        "/auth/v1/tenants/{tenantId}/invitations",
        "/auth/v1/tenants/{tenantId}/invitations/{invitationId}",
        "/auth/v1/tenants/{tenantId}/members",
        "/auth/v1/tenants/{tenantId}/members/{userId}",
        "/auth/v1/token",
        "/auth/v1/tokens",
        "/auth/v1/tokens/{keyId}",
        "/auth/v1/user",
    ];

    private static readonly string[] _selfServiceOperations =
    [
        "tenants.list", "tenants.create", "tenants.rename", "tenants.listMembers", "tenants.updateMember",
        "tenants.removeMember", "invitations.preview", "invitations.accept", "tenants.createInvitation",
        "tenants.listInvitations", "tenants.revokeInvitation", "tokens.create", "tokens.list", "tokens.revoke",
    ];

    private static async Task<JsonNode> GeneratedAsync()
    {
        var openApi = await AccountsReferenceGenerator.CaptureAsync(RepositoryRoot.Find(), TestContext.Current.CancellationToken);
        return JsonNode.Parse(AccountsReferenceGenerator.Render(openApi).Content)!;
    }

    private static string[] PathsOf(JsonNode document) => [.. document["paths"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_reference_holds_exactly_the_sign_in_routes() =>
        PathsOf(await GeneratedAsync()).ShouldBe(_signInPaths);

    [Fact]
    public async Task No_entity_route_reaches_the_sign_in_reference() =>
        PathsOf(await GeneratedAsync()).ShouldAllBe(path => path.StartsWith("/auth/v1", StringComparison.Ordinal));

    [Fact]
    public async Task Every_operation_keeps_its_operation_id()
    {
        var ids = (await GeneratedAsync())["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(operation => operation.Value!["operationId"]?.GetValue<string>()))
            .ToList();

        foreach (var expected in _selfServiceOperations)
        {
            ids.Count(id => id == expected).ShouldBe(1, expected);
        }
    }

    [Fact]
    public void Filter_keeps_components_and_titles_the_document()
    {
        var source = JsonNode.Parse("""
            { "info": { "title": "Alvo", "version": "v1" },
              "paths": { "/auth/v1/user": {}, "/api/owners": {} },
              "components": { "schemas": { "X": {} } } }
            """)!;

        var document = AccountsApiDocument.Filter(source, "/auth/v1");

        PathsOf(document).ShouldBe(["/auth/v1/user"]);
        document["components"]!["schemas"]!["X"].ShouldNotBeNull();
        document["info"]!["title"]!.GetValue<string>().ShouldBe("Alvo sign-in, tenants and tokens — example (field-service)");
        source["paths"]!.AsObject().Count.ShouldBe(2);
    }

    [Fact]
    public void The_description_links_only_existing_pages()
    {
        var docs = Path.Combine(RepositoryRoot.Find(), "website", "src", "content", "docs");

        Should.NotThrow(() => HostPagesGenerator.RequireLinkTargets(AccountsApiDocument.Description, docs));
    }
}
