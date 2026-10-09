using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Xml;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Host;

internal sealed class HostPagesGenerator : IPageGenerator
{
    internal const string ExampleNotice =
        "The Data API Alvo generated for the vehicle-registry example descriptor. "
        + "Every descriptor generates its own document at `GET /openapi/v1.json` (with a UI at `/scalar`); "
        + "see [Data API conventions](/MMLib.Alvo/data-api/conventions/).";

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    public async Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        var snapshot = await context.Host.Value.WaitAsync(ct).ConfigureAwait(false);
        return Render(snapshot, XmlDocs.Load(ShippedAssemblies.All));
    }

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
        return new GeneratedPage(OutputRoot.Generated, "openapi/data-api.json", document.ToJsonString(_indented));
    }
}
