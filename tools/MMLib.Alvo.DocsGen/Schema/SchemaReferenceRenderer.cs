using MMLib.Alvo.DocsGen.Markdown;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Schema;

internal static partial class SchemaReferenceRenderer
{
    internal const int MaxKeySectionsPerPage = 60;

    private const string DescriptorFolder = "descriptor";
    private const string IndexSlug = "index";

    internal static string PageSlug(string topLevelKey) => DescriptorPageSplit.Slug(topLevelKey);

    internal static IReadOnlyList<GeneratedPage> Render(JsonObject schema) => Render(schema, DefaultGuideTitle);

    internal static IReadOnlyList<GeneratedPage> Render(JsonObject schema, Func<string, string> guideTitle)
    {
        var walker = new SchemaWalker(schema);
        var guides = new GuideLine(guideTitle);
        var pages = new List<GeneratedPage> { DescriptorIndex(walker, guides) };
        var keys = walker.TopLevelKeys;
        var extraPages = 0;
        for (var index = 0; index < keys.Count; index++)
        {
            if (walker.IsBlock(keys[index]))
            {
                var blockPages = BlockPages(walker, keys[index], index + extraPages, guides);
                pages.AddRange(blockPages);
                extraPages += blockPages.Count - 1;
            }
        }

        return pages;
    }

    internal static IReadOnlyList<string> UnrecognisedConditions(JsonObject schema)
    {
        var walker = new SchemaWalker(schema);
        foreach (var block in walker.TopLevelKeys.Where(walker.IsBlock))
        {
            walker.KeysOf(block);
        }

        return walker.UnrecognisedConditions;
    }

    internal static string DefaultGuideTitle(string guideSlug)
    {
        var name = guideSlug[(guideSlug.LastIndexOf('/') + 1)..].Replace('-', ' ');
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static GeneratedPage DescriptorIndex(SchemaWalker walker, GuideLine guides)
    {
        var page = new StringBuilder(Md.Frontmatter("Descriptor reference", FirstSentence(walker.Description), 0, editSource: SchemaPagesGenerator.SchemaRepoPath))
            .Append(guides.For(IndexSlug)).Append("\n\n")
            .Append(Md.Text(walker.Description)).Append("\n\n")
            .Append("## Top-level keys\n\nBlocks, each documented on its own page:\n\n");
        AppendBlockLinks(page, walker);
        foreach (var note in walker.RootNotes)
        {
            page.Append(note).Append("\n\n");
        }

        foreach (var key in walker.TopLevelKeys.Where(key => !walker.IsBlock(key)))
        {
            SchemaSection.Append(page, walker.KeysOf(key)[0]);
        }

        return Descriptor(IndexSlug, page);
    }

    private static void AppendBlockLinks(StringBuilder page, SchemaWalker walker)
    {
        foreach (var key in walker.TopLevelKeys.Where(walker.IsBlock))
        {
            var description = FirstSentence(walker.KeysOf(key)[0].Description);
            page.Append("- [").Append(Md.Code(key)).Append("](").Append(DescriptorLink(PageSlug(key))).Append(") — ").Append(Md.Text(description)).Append('\n');
            if (key == DescriptorPageSplit.EntitiesKey)
            {
                AppendEntityPageLinks(page);
            }
        }

        page.Append('\n');
    }

    private static void AppendEntityPageLinks(StringBuilder page)
    {
        foreach (var (slug, label, _) in DescriptorPageSplit.EntityPages.Skip(1))
        {
            page.Append("  - [").Append(label).Append("](").Append(DescriptorLink(slug)).Append(")\n");
        }
    }

    private static List<GeneratedPage> BlockPages(SchemaWalker walker, string key, int order, GuideLine guides)
    {
        var keys = walker.KeysOf(key);
        if (key != DescriptorPageSplit.EntitiesKey)
        {
            return [BlockPage(new PageSpec(PageSlug(key), key, key, order), keys, guides, siblings: null)];
        }

        return [.. DescriptorPageSplit.EntityPages.Select((entry, offset) =>
            BlockPage(new PageSpec(entry.Slug, entry.Title, entry.Label, order + offset), keys.Where(k => DescriptorPageSplit.PageOf(k.Path) == entry.Slug).ToList(), guides, entry.Slug))];
    }

    private static GeneratedPage BlockPage(PageSpec spec, IReadOnlyList<SchemaKey> keys, GuideLine guides, string? siblings)
    {
        var description = keys.Count > 0 ? FirstSentence(keys[0].Description) : spec.Title;
        var page = new StringBuilder(Md.Frontmatter(spec.Title, description, spec.Order, sidebarLabel: spec.Label, tocMaxHeadingLevel: 2, editSource: SchemaPagesGenerator.SchemaRepoPath))
            .Append(guides.For(spec.Slug)).Append("\n\n");
        if (siblings is not null)
        {
            page.Append(SiblingLine(siblings)).Append("\n\n");
        }

        var ownSection = keys.Count > 0 && !keys[0].Path.Contains('.', StringComparison.Ordinal);
        if (ownSection)
        {
            SchemaSection.Append(page, keys[0]);
        }

        page.Append("## Keys\n\n");
        foreach (var key in keys.Skip(ownSection ? 1 : 0))
        {
            SchemaSection.Append(page, key);
        }

        return Descriptor(spec.Slug, page);
    }

    private static string SiblingLine(string current) =>
        "The `entities` block spans these pages: " + string.Join(" · ", DescriptorPageSplit.EntityPages.Select(entry =>
            entry.Slug == current ? $"**{entry.Label}**" : $"[{entry.Label}]({DescriptorLink(entry.Slug)})")) + ".";

    private static GeneratedPage Descriptor(string slug, StringBuilder page) =>
        new(OutputRoot.Reference, $"{DescriptorFolder}/{slug}.md", page.ToString().TrimEnd('\n') + "\n");

    private static string DescriptorLink(string slug) => SiteLinks.Page($"reference/{DescriptorFolder}/{slug}");

    private static string FirstSentence(string text)
    {
        var collapsed = Whitespace().Replace(text, " ").Trim();
        var match = SentenceEnd().Match(collapsed);
        return match.Success ? collapsed[..(match.Index + 1)] : collapsed;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<!\be\.g|\bi\.e)[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();

    private sealed record PageSpec(string Slug, string Title, string Label, int Order);

    private sealed class GuideLine(Func<string, string> guideTitle)
    {
        internal string For(string pageSlug) => GuideMap.GuideOf(pageSlug) is { } guide
            ? $"**Guide:** [{Md.Text(guideTitle(guide))}]({SiteLinks.Page(guide)})"
            : $"**Not in this build:** see [What works today]({SiteLinks.Page("start-here/what-works-today")}).";
    }
}
