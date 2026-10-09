using MMLib.Alvo.DocsGen.Markdown;
using MMLib.Alvo.DocsGen.Schema;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Host;

internal static class CapabilitiesRenderer
{
    private const string DescriptorFolder = "descriptor";
    private const string FrontmatterEnd = "\n---\n\n";
    private const string WarnedTitle = "Declared but not run in this build";
    private const string RefusedTitle = "Refused at apply";

    private static readonly Dictionary<string, string> _prefixPages = new(StringComparer.Ordinal)
    {
        ["entity."] = "entities",
        ["field."] = "entities-fields",
        ["rollup."] = "entities-computed-and-rollups",
        ["email."] = "entities-hooks",
        ["trigger."] = "automation",
        ["auth."] = "auth",
        ["http."] = "entities-hooks",
    };

    private static readonly Dictionary<string, string> _slotPages = new(StringComparer.Ordinal)
    {
        ["entity.update"] = "entities-hooks",
        ["function"] = "entities-hooks",
        ["JSONata"] = "entities-hooks",
        ["bodyFile"] = "templates",
    };

    internal static GeneratedPage Render(JsonNode capabilities)
    {
        var page = new StringBuilder(Md.Frontmatter("Capabilities in this build", "What this build honours, what it parses and does not run, and what it refuses at apply.", 6))
            .Append("This page is generated from `GET {management}/projects/{project}/capabilities`, the answer the dashboard and the schema assistant read. ")
            .Append("Each sentence below is the framework's own, copied verbatim.\n\n")
            .Append("## Honoured\n\n");
        foreach (var block in capabilities["honoured"]!.AsArray())
        {
            page.Append("- ").Append(Md.Code(block!.GetValue<string>())).Append('\n');
        }

        AppendEntries(page, WarnedTitle, Warned(capabilities));
        AppendEntries(page, RefusedTitle, Refused(capabilities));
        return new GeneratedPage(OutputRoot.Reference, "capabilities.md", page.ToString());
    }

    internal static IReadOnlyList<GeneratedPage> WithAsides(IReadOnlyList<GeneratedPage> pages, JsonNode capabilities)
    {
        var warned = Warned(capabilities).ToLookup(entry => PageOf(entry.Name));
        var refused = Refused(capabilities).ToLookup(entry => PageOf(entry.Name));
        RefuseMissingPages(pages, warned.Select(group => group.Key).Concat(refused.Select(group => group.Key)));
        return [.. pages.Select(page => WithAside(page, Asides(warned[page.RelativePath], refused[page.RelativePath])))];
    }

    internal static string PageOf(string slot) => $"{DescriptorFolder}/{PageSlugOf(slot)}.md";

    private static string PageSlugOf(string slot)
    {
        if (_slotPages.TryGetValue(slot, out var page))
        {
            return page;
        }

        if (!slot.Contains('.', StringComparison.Ordinal))
        {
            return DescriptorPageSplit.Slug(slot);
        }

        return _prefixPages.FirstOrDefault(prefix => slot.StartsWith(prefix.Key, StringComparison.Ordinal)).Value
            ?? throw new InvalidOperationException($"The capability slot '{slot}' has no descriptor page; add its prefix to CapabilitiesRenderer.");
    }

    private static bool IsTopLevelBlock(string slot) => !_slotPages.ContainsKey(slot) && !slot.Contains('.', StringComparison.Ordinal);

    private static void RefuseMissingPages(IReadOnlyList<GeneratedPage> pages, IEnumerable<string> targets)
    {
        var missing = targets.Except(pages.Select(page => page.RelativePath), StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Capability entries point at descriptor pages that were not generated: {string.Join(", ", missing)}.");
        }
    }

    private static string Asides(IEnumerable<Entry> warned, IEnumerable<Entry> refused) =>
        Aside("caution", "Not run in this build", warned) + Aside("danger", RefusedTitle, refused);

    private static string Aside(string kind, string title, IEnumerable<Entry> entries)
    {
        var lines = entries.Select(entry => IsTopLevelBlock(entry.Name) ? entry.Text : entry.Line).ToList();
        return lines.Count == 0 ? string.Empty : $":::{kind}[{title}]\n{string.Join("\n\n", lines)}\n:::\n\n";
    }

    private static GeneratedPage WithAside(GeneratedPage page, string asides)
    {
        if (asides.Length == 0)
        {
            return page;
        }

        var end = page.Content.IndexOf(FrontmatterEnd, StringComparison.Ordinal) + FrontmatterEnd.Length;
        return page with { Content = page.Content[..end] + asides + page.Content[end..] };
    }

    private static void AppendEntries(StringBuilder page, string title, IEnumerable<Entry> entries)
    {
        page.Append("\n## ").Append(title).Append("\n\n");
        foreach (var entry in entries)
        {
            page.Append("- ").Append(entry.Line).Append('\n');
        }
    }

    private static IEnumerable<Entry> Warned(JsonNode capabilities) =>
        capabilities["warned"]!.AsArray().Select(node => new Entry(Text(node!, "block"), Md.Text(Text(node!, "consequence"))));

    private static IEnumerable<Entry> Refused(JsonNode capabilities) =>
        capabilities["refused"]!.AsArray().Select(node => new Entry(Text(node!, "slot"), $"{Md.Text(Text(node!, "consequence"))} **Fix:** {Md.Text(Text(node!, "fix"))}"));

    private static string Text(JsonNode node, string property) => node[property]!.GetValue<string>();

    private sealed record Entry(string Name, string Text)
    {
        internal string Line => $"{Md.Code(Name)} — {Text}";
    }
}
