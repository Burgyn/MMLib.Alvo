using Microsoft.AspNetCore.Builder;
using MMLib.Alvo.DocsGen.Exchanges;
using MMLib.Alvo.Host;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Host;

/// <summary>Publishes the sign-in routes from the OpenAPI document of a second in-process host, one with accounts on (plan D13).</summary>
internal sealed class AccountsReferenceGenerator : IPageGenerator
{
    private const string KeyId = "docsgen";

    private static readonly ExchangeKey _admin = new(["admin", "authenticated"], ["*:read", "*:write"], null, "ALVO_KEY_SECRET");

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true, NewLine = "\n" };

    public async Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        var openApi = await CaptureAsync(context.Paths.RepoRoot, ct).ConfigureAwait(false);
        var page = Render(openApi);
        HostPagesGenerator.RequireLinkTargets(AccountsApiDocument.Description, context.Paths.DocsDir);
        return [page];
    }

    internal static GeneratedPage Render(JsonNode openApi)
    {
        var document = AccountsApiDocument.Filter(openApi, AccountsApiDocument.Prefix);
        document["servers"] = new JsonArray(new JsonObject
        {
            ["url"] = ExchangeRenderer.DisplayBase,
            ["description"] = HostPagesGenerator.ServerDescription,
        });
        return new GeneratedPage(OutputRoot.Generated, AccountsApiDocument.DocumentPath, document.ToJsonString(_indented));
    }

    internal static async Task<JsonNode> CaptureAsync(string repoRoot, CancellationToken ct)
    {
        var descriptor = Path.Combine(repoRoot, "examples", "field-service", "field-service.alvo.json");
        var keys = new Dictionary<string, ExchangeKey>(StringComparer.Ordinal) { [KeyId] = _admin };
        var host = await HostBoot.StartAsync(descriptor, keys, ct, withAccounts: true).ConfigureAwait(false);
        await using (host.ConfigureAwait(false))
        {
            using var client = host.Client(KeyId);
            using var response = await client.GetAsync(new Uri(AlvoHost.OpenApiDocumentPath, UriKind.Relative), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                ?? throw new InvalidOperationException($"GET {AlvoHost.OpenApiDocumentPath} answered an empty document.");
        }
    }
}
