using MMLib.Alvo.DocsGen.Markdown;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Examples;

internal sealed partial class ExamplesGenerator : IPageGenerator
{
    internal const string PageFile = "examples.md";

    internal const string ImageExamplesRoot = "/alvo/examples";

    private const string QuickStartCompose = "docker compose -f docker-compose.quickstart.yml";

    public Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GeneratedPage>>([Render(ExampleCatalog.Read(context.Paths.RepoRoot))]);

    internal static GeneratedPage Render(IReadOnlyList<Example> examples)
    {
        var page = new StringBuilder(Md.Frontmatter(
                "Examples",
                "Every example descriptor in the repository: what it shows, whether it applies, and how to run it.",
                editSource: ExampleCatalog.ReadmePath))
            .Append("Each example lives under `examples/` in the repository and is validated against the descriptor schema on every build. ")
            .Append("The ones that apply also ship inside the image, under `").Append(ImageExamplesRoot).Append("/`, so the [Quick start](")
            .Append(SiteLinks.Page("start-here/quick-start")).Append(")'s compose file serves any of them with `ALVO_DESCRIPTOR` and no clone. ")
            .Append("Each summary comes from [`examples/README.md`](")
            .Append(SiteLinks.RepoBlob(ExampleCatalog.ReadmePath)).Append("), which also lists the keys the schema declares but this build refuses at apply.\n");
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
            .Append("**Tenancy:** ").Append(example.MultiTenant ? "multi-tenant (`tenancy.enabled: true`)" : "single-tenant").Append("\n\n")
            .Append("**Descriptor:** [`").Append(directory).Append('/').Append(example.Descriptor).Append("`](")
            .Append(SiteLinks.RepoBlob($"{directory}/{example.Descriptor}")).Append(")\n");
        if (!example.Runnable)
        {
            return;
        }

        AppendRun(page, example);
        if (example.OwnStack is { } stack)
        {
            AppendOwnStack(page, stack, directory);
        }
        else
        {
            AppendKeys(page);
        }
    }

    internal static string Sentence(string summary)
    {
        var parts = summary.Split('`');
        for (var index = 0; index < parts.Length; index += 2)
        {
            parts[index] = parts[index].Replace("*", string.Empty, StringComparison.Ordinal);
        }

        var plain = Spaces().Replace(AppliesAsItStands().Replace(string.Join('`', parts), string.Empty), " ").Trim();
        return plain.Length == 0 ? plain : char.ToUpperInvariant(plain[0]) + plain[1..];
    }

    private static string Applies(Example example, string directory) =>
        example.Runnable ? "yes" : $"no — [why]({SiteLinks.RepoBlob($"{directory}/{ExampleCatalog.NotRunnableMarker}")})";

    private static string Roles(Example example) =>
        example.Roles.Count == 0 ? "none declared; `authenticated` is enough" : string.Join(", ", example.Roles.Select(Md.Code));

    internal static string InImagePath(Example example) => $"{ImageExamplesRoot}/{example.Directory}/{example.Descriptor}";

    private static void AppendRun(StringBuilder page, Example example) =>
        page.Append("\nIn the image at `").Append(InImagePath(example)).Append("`. Serve it with the quick start's compose file, ")
            .Append("from the directory that holds it; the `down` deletes the database of the descriptor it served before:\n\n```sh\n")
            .Append(QuickStartCompose).Append(" down --volumes\n")
            .Append("ALVO_DESCRIPTOR=").Append(InImagePath(example)).Append(' ').Append(QuickStartCompose).Append(" up --wait\n```\n");

    private static void AppendKeys(StringBuilder page) =>
        page.Append("\nThe quick start's `demo` key holds only the built-in roles `admin` and `authenticated`, so it authenticates here; ")
            .Append("for keys with this example's own roles, use an override as [Run your own descriptor](")
            .Append(SiteLinks.Page("start-here/run-your-own")).Append(") shows.\n");

    private static void AppendOwnStack(StringBuilder page, ExampleStack stack, string directory) =>
        page.Append("\nThe quick start's `demo` key belongs to no tenant, so there only the entities marked `tenancy: global` answer; ")
            .Append("the header of `docker-compose.quickstart.yml` says how to give the key a tenant. ")
            .Append("The example's own stack, `").Append(stack.ComposeFile).Append("`, has one dev key per role and tenant and runs from a clone ")
            .Append("of the repository. Generate a secret for each key and start it, as its [README](").Append(SiteLinks.RepoBlob($"{directory}/README.md"))
            .Append(") describes:\n\n```sh\n").Append(stack.Command()).Append("\n```\n");

    [GeneratedRegex(@"\bapplies as it stands\.\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AppliesAsItStands();

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();
}
