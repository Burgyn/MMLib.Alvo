namespace MMLib.Alvo.DocsGen.Repo;

internal sealed class RepoPagesGenerator : IPageGenerator
{
    internal static IReadOnlyList<(string Source, string Target, string Title, string Description)> Pages { get; } =
    [
        ("CHANGELOG.md", "project/changelog.md", "Changelog", "Every notable change, rendered from CHANGELOG.md."),
        ("CONTRIBUTING.md", "project/contributing.md", "Contributing", "How to contribute to Alvo, rendered from CONTRIBUTING.md."),
    ];

    public async Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        var pages = new List<GeneratedPage>();
        foreach (var (source, target, title, description) in Pages)
        {
            var markdown = await File.ReadAllTextAsync(Path.Combine(context.Paths.RepoRoot, source), ct).ConfigureAwait(false);
            pages.Add(new GeneratedPage(OutputRoot.Docs, target, RepoMarkdownPage.Render(markdown, title, description, source)));
        }

        return pages;
    }
}
