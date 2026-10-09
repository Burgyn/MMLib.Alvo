namespace MMLib.Alvo.DocsGen.Exchanges;

internal sealed class ExchangesGenerator : IPageGenerator
{
    internal const string SpecSuffix = ".exchange.json";

    private const string ExchangesSegment = "/src/exchanges/";

    public async Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        var pages = new List<GeneratedPage>();
        foreach (var path in SpecFiles(context.Paths.SiteRoot))
        {
            var spec = ExchangeSpec.Parse(NameOf(path), await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            var steps = await ExchangeRunner.RunAsync(spec, context.Paths.RepoRoot, ct).ConfigureAwait(false);
            pages.Add(ExchangeRenderer.Render(spec, steps));
        }

        return pages;
    }

    internal static IReadOnlyList<string> SpecFiles(string siteRoot)
    {
        var root = Path.Combine(siteRoot, "src", "exchanges");
        return Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*" + SpecSuffix, SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
            : [];
    }

    internal static string NameOf(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        var start = normalized.LastIndexOf(ExchangesSegment, StringComparison.Ordinal);
        if (start < 0 || !normalized.EndsWith(SpecSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{path}' is not an exchange spec under website/src/exchanges/ named *{SpecSuffix}.");
        }

        return normalized[(start + ExchangesSegment.Length)..^SpecSuffix.Length];
    }
}
