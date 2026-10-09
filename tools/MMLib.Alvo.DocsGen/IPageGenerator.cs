namespace MMLib.Alvo.DocsGen;

internal interface IPageGenerator
{
    Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct);
}

internal sealed class DocsGenContext(DocsGenPaths paths)
{
    internal DocsGenPaths Paths { get; } = paths;
}
