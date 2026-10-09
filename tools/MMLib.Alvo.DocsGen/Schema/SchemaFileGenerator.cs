namespace MMLib.Alvo.DocsGen.Schema;

internal sealed class SchemaFileGenerator : IPageGenerator
{
    internal const string PublishedPath = "schema/v1/project.json";

    public async Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct) =>
    [
        new GeneratedPage(OutputRoot.Public, PublishedPath, await File.ReadAllTextAsync(SchemaPagesGenerator.SchemaPath(context.Paths), ct).ConfigureAwait(false)),
    ];
}
