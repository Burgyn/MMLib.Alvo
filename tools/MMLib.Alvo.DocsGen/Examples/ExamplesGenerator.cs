using MMLib.Alvo.DocsGen.Markdown;
using System.Text;

namespace MMLib.Alvo.DocsGen.Examples;

internal sealed class ExamplesGenerator : IPageGenerator
{
    internal const string PageFile = "examples.md";

    public Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GeneratedPage>>([Render(ExampleCatalog.Read(context.Paths.RepoRoot))]);

    internal static GeneratedPage Render(IReadOnlyList<Example> examples)
    {
        var page = new StringBuilder(Md.Frontmatter(
                "Examples",
                "Every example descriptor in the repository: what it shows, whether it applies, and how to run it."))
            .Append("Each example lives under `examples/` in the repository and is validated against the descriptor schema on every build. ")
            .Append("The ones that apply can be started with the standalone stack. Each summary comes from [`examples/README.md`](")
            .Append(SiteLinks.RepoBlob("examples/README.md")).Append("), which also lists the keys the schema declares but this build refuses at apply.\n");
        foreach (var example in examples.OrderByDescending(example => example.Runnable).ThenBy(example => example.Directory, StringComparer.Ordinal))
        {
            AppendExample(page, example);
        }

        return new GeneratedPage(OutputRoot.Docs, PageFile, page.ToString());
    }

    private static void AppendExample(StringBuilder page, Example example)
    {
        var directory = $"examples/{example.Directory}";
        page.Append("\n## ").Append(example.Directory).Append("\n\n")
            .Append(Md.Text(Sentence(example.Summary))).Append("\n\n")
            .Append("**Applies:** ").Append(Applies(example, directory)).Append("\n\n")
            .Append("**Entities:** ").Append(string.Join(", ", example.Entities.Select(Md.Code))).Append("\n\n")
            .Append("**Roles a key needs:** ").Append(Roles(example)).Append("\n\n")
            .Append("**Descriptor:** [`").Append(directory).Append('/').Append(example.Descriptor).Append("`](")
            .Append(SiteLinks.RepoBlob($"{directory}/{example.Descriptor}")).Append(")\n");
        if (example.Runnable)
        {
            AppendRun(page, example, directory);
        }
    }

    private static string Sentence(string summary)
    {
        var plain = summary.Replace("**", string.Empty, StringComparison.Ordinal);
        return plain.Length == 0 ? plain : char.ToUpperInvariant(plain[0]) + plain[1..];
    }

    private static string Applies(Example example, string directory) =>
        example.Runnable ? "yes" : $"no — [why]({SiteLinks.RepoBlob($"{directory}/{ExampleCatalog.NotRunnableMarker}")})";

    private static string Roles(Example example) =>
        example.Roles.Count == 0 ? "none declared; `authenticated` is enough" : string.Join(", ", example.Roles.Select(Md.Code));

    private static void AppendRun(StringBuilder page, Example example, string directory) =>
        page.Append("\nSet the key secret and declare keys with these roles as [Run your own descriptor](")
            .Append(SiteLinks.Page("start-here/run-your-own")).Append(") shows, then start the stack over this descriptor:\n\n")
            .Append("```sh\nALVO_DESCRIPTOR=./").Append(directory).Append('/').Append(example.Descriptor)
            .Append(" docker compose up --build --wait\n```\n");
}
