using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Schema;

internal sealed class SchemaPagesGenerator : IPageGenerator
{
    public async Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        var schema = await LoadAsync(context.Paths, ct).ConfigureAwait(false);
        var unrecognised = SchemaReferenceRenderer.UnrecognisedConditions(schema);
        if (unrecognised.Count > 0)
        {
            throw new InvalidOperationException(
                "The descriptor schema has conditionals the reference cannot describe; teach SchemaConditions their shape:\n"
                + string.Join('\n', unrecognised));
        }

        var titles = new GuideTitles(context.Paths.DocsDir);
        return SchemaReferenceRenderer.Render(schema, titles.TitleOf);
    }

    internal static string SchemaPath(DocsGenPaths paths) => Path.Combine(paths.RepoRoot, "schema", "project.schema.json");

    private static async Task<JsonObject> LoadAsync(DocsGenPaths paths, CancellationToken ct) =>
        JsonNode.Parse(await File.ReadAllTextAsync(SchemaPath(paths), ct).ConfigureAwait(false))!.AsObject();
}
