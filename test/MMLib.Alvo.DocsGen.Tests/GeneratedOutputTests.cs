using MMLib.Alvo.DocsGen.Configuration;
using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Examples;
using MMLib.Alvo.DocsGen.Host;
using MMLib.Alvo.DocsGen.Limits;
using MMLib.Alvo.DocsGen.Problems;
using MMLib.Alvo.DocsGen.Repo;
using MMLib.Alvo.DocsGen.Schema;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Tests;

/// <summary>
/// Runs the real generators over the real repository and a real host, and reads what a site visitor would read.
/// The changelog and the contributing guide are left out of the wording check on purpose: they are written for
/// contributors and name roadmap phases legitimately.
/// </summary>
public partial class GeneratedOutputTests
{
    private static readonly Lazy<Task<IReadOnlyList<GeneratedPage>>> _pages = new(GenerateAsync);

    [Fact]
    public async Task No_reader_page_names_a_roadmap_phase_or_the_spec()
    {
        var offending = (await ReaderPagesAsync())
            .SelectMany(page => Contributor().Matches(page.Content).Select(match => $"{page.RelativePath}: {Around(page.Content, match)}"))
            .ToList();

        offending.ShouldBeEmpty(string.Join('\n', offending));
    }

    [Fact]
    public async Task Every_generated_page_turns_off_the_edit_link_or_points_it_at_a_real_source()
    {
        var root = RepositoryRoot.Find();
        var pages = (await _pages.Value).Where(IsMarkdown).ToList();

        pages.ShouldNotBeEmpty();
        foreach (var page in pages)
        {
            var editUrl = EditUrl().Match(page.Content);
            editUrl.Success.ShouldBeTrue($"{page.RelativePath} has no editUrl, so Starlight would link to its gitignored path.");
            if (editUrl.Groups["source"].Success)
            {
                File.Exists(Path.Combine(root, editUrl.Groups["source"].Value)).ShouldBeTrue($"{page.RelativePath} edits a file that does not exist.");
            }
        }
    }

    [Fact]
    public async Task Pages_rendered_from_one_file_edit_that_file()
    {
        var pages = await _pages.Value;

        pages.Single(page => page.RelativePath == "project/changelog.md").Content
            .ShouldContain("editUrl: \"https://github.com/Burgyn/MMLib.Alvo/edit/main/CHANGELOG.md\"\n");
        pages.Single(page => page.RelativePath == ExamplesGenerator.PageFile).Content
            .ShouldContain("editUrl: \"https://github.com/Burgyn/MMLib.Alvo/edit/main/examples/README.md\"\n");
        pages.Single(page => page.RelativePath == "descriptor/index.md").Content
            .ShouldContain("editUrl: \"https://github.com/Burgyn/MMLib.Alvo/edit/main/schema/project.schema.json\"\n");
        pages.Single(page => page.RelativePath == "capabilities.md").Content.ShouldContain("editUrl: false\n");
    }

    private static async Task<IEnumerable<GeneratedPage>> ReaderPagesAsync() =>
        (await _pages.Value).Where(page => (IsMarkdown(page) && page.Root == OutputRoot.Reference) || page.RelativePath == ExamplesGenerator.PageFile);

    private static bool IsMarkdown(GeneratedPage page) => page.RelativePath.EndsWith(".md", StringComparison.Ordinal);

    private static string Around(string content, Match match) =>
        content[Math.Max(0, match.Index - 60)..Math.Min(content.Length, match.Index + match.Length + 30)].ReplaceLineEndings(" ");

    private static async Task<IReadOnlyList<GeneratedPage>> GenerateAsync()
    {
        var root = RepositoryRoot.Find();
        var site = Path.Combine(root, "website");
        var context = new DocsGenContext(new DocsGenPaths(root, site, Path.Combine(site, "src", "content", "docs", "reference")));
        IPageGenerator[] generators =
        [
            new SchemaPagesGenerator(), new ProblemTypesGenerator(), new ConfigurationGenerator(), new LimitsGenerator(),
            new HostPagesGenerator(), new CSharpApiGenerator(), new RepoPagesGenerator(), new ExamplesGenerator(),
        ];
        var pages = new List<GeneratedPage>();
        foreach (var generator in generators)
        {
            pages.AddRange(await generator.GenerateAsync(context, CancellationToken.None));
        }

        return pages;
    }

    [GeneratedRegex(@"\bF\d\b|\bthe spec\b|\bdesign's D\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex Contributor();

    [GeneratedRegex("""^editUrl: (?:false|"https://github\.com/Burgyn/MMLib\.Alvo/edit/main/(?<source>[^"]+)")$""", RegexOptions.Multiline)]
    private static partial Regex EditUrl();
}
