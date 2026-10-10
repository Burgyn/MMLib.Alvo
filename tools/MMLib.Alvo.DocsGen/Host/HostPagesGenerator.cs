using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Exchanges;
using MMLib.Alvo.DocsGen.Markdown;
using MMLib.Alvo.DocsGen.Xml;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Host;

internal sealed partial class HostPagesGenerator : IPageGenerator
{
    internal const string ExampleNotice =
        "The Data API Alvo generated for the vehicle-registry example descriptor. "
        + "Every descriptor generates its own document at `GET /openapi/v1.json` (with a UI at `/scalar`); "
        + "see [Data API conventions](" + SiteLinks.BasePath + "/data-api/conventions/).";

    internal const string ServerDescription = "The default address of a standalone host (docker compose, or the container image on port 8080).";

    private const string DataApiDocumentPath = "openapi/data-api.json";

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true, NewLine = "\n" };

    public async Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        var snapshot = await context.Host.Value.WaitAsync(ct).ConfigureAwait(false);
        var pages = Render(snapshot, XmlDocs.Load(ShippedAssemblies.All));
        RequireLinkTargets(DescriptionOf(pages), context.Paths.DocsDir);
        return pages;
    }

    internal static string DescriptionOf(IReadOnlyList<GeneratedPage> pages) =>
        JsonNode.Parse(pages.Single(page => page.RelativePath == DataApiDocumentPath).Content)!["info"]!["description"]!.GetValue<string>();

    internal static void RequireLinkTargets(string markdown, string docsDir)
    {
        var missing = SiteLinkPattern().Matches(markdown)
            .Select(match => match.Groups["slug"].Value.Trim('/') is { Length: > 0 } slug ? slug : "index")
            .Where(slug => !PageExists(docsDir, slug))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The Data API document's description links to pages that do not exist under {docsDir}: {string.Join(", ", missing)}.");
        }
    }

    private static bool PageExists(string docsDir, string slug) =>
        new[] { $"{slug}.md", $"{slug}.mdx", $"{slug}/index.md", $"{slug}/index.mdx" }
            .Any(candidate => File.Exists(Path.Combine(docsDir, candidate)));

    internal static IReadOnlyList<GeneratedPage> Render(HostSnapshot snapshot, XmlDocs docs) =>
    [
        CelCatalogRenderer.Render(snapshot.CelFunctions),
        ManagementApiRenderer.Render(snapshot.ManagementRoutes, docs),
        CapabilitiesRenderer.Render(snapshot.Capabilities),
        DataApiDocument(snapshot.OpenApi),
    ];

    private static GeneratedPage DataApiDocument(JsonNode openApi)
    {
        var document = openApi.DeepClone();
        var info = document["info"]!.AsObject();
        var captured = info["description"]?.GetValue<string>();
        info["description"] = captured is { Length: > 0 } ? $"{ExampleNotice}\n\n{captured}" : ExampleNotice;
        document["servers"] = new JsonArray(new JsonObject { ["url"] = ExchangeRenderer.DisplayBase, ["description"] = ServerDescription });
        return new GeneratedPage(OutputRoot.Generated, DataApiDocumentPath, document.ToJsonString(_indented));
    }

    [GeneratedRegex(@"\]\(" + SiteLinks.BasePath + @"(?<slug>/[^)\s#?]*)", RegexOptions.CultureInvariant)]
    private static partial Regex SiteLinkPattern();
}
