using MMLib.Alvo.DocsGen.Markdown;
using System.Globalization;
using System.Text;

namespace MMLib.Alvo.DocsGen;

internal sealed class ReferenceIndexGenerator : IPageGenerator
{
    private const string IndexFile = "index.md";

    public Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GeneratedPage>>([Render(Scan(context.Paths.ReferenceDir))]);

    internal static GeneratedPage Render(IEnumerable<(string Slug, string Title, string Summary)> pages)
    {
        var body = new StringBuilder(Md.Frontmatter("Reference", "Generated from the code: every page in this section is produced by tools/MMLib.Alvo.DocsGen at build time.", 0));
        foreach (var (slug, title, summary) in pages)
        {
            body.Append("- [").Append(Md.Text(title)).Append("](").Append(SiteLinks.Page($"reference/{slug}")).Append(") — ").Append(Md.Text(summary)).Append('\n');
        }

        return new GeneratedPage(OutputRoot.Reference, IndexFile, body.ToString());
    }

    internal static IReadOnlyList<(string Slug, string Title, string Summary)> Scan(string referenceDir) =>
    [
        .. PageFiles(referenceDir)
            .Select(file => (Slug: SlugOf(referenceDir, file), Front: Frontmatter.Read(file)))
            .OrderBy(page => page.Front.Order)
            .ThenBy(page => page.Front.Title, StringComparer.Ordinal)
            .Select(page => (page.Slug, page.Front.Title, page.Front.Description)),
    ];

    private static IEnumerable<string> PageFiles(string referenceDir)
    {
        if (!Directory.Exists(referenceDir))
        {
            return [];
        }

        var topLevel = Directory.EnumerateFiles(referenceDir, "*.md").Where(file => Path.GetFileName(file) != IndexFile);
        var sectionIndexes = Directory.EnumerateDirectories(referenceDir).Select(directory => Path.Combine(directory, IndexFile)).Where(File.Exists);
        return topLevel.Concat(sectionIndexes);
    }

    private static string SlugOf(string referenceDir, string file)
    {
        var relative = Path.GetRelativePath(referenceDir, file).Replace(Path.DirectorySeparatorChar, '/');
        var withoutExtension = relative[..^".md".Length];
        return withoutExtension.EndsWith("/index", StringComparison.Ordinal) ? withoutExtension[..^"/index".Length] : withoutExtension;
    }

    private sealed record Frontmatter(string Title, string Description, int Order)
    {
        internal static Frontmatter Read(string file)
        {
            var lines = File.ReadLines(file).Skip(1).TakeWhile(line => line != "---").ToList();
            return new Frontmatter(
                Value(lines, "title:") ?? throw new InvalidOperationException($"'{file}' has no title in its frontmatter."),
                Value(lines, "description:") ?? string.Empty,
                Value(lines, "  order:") is { } order ? int.Parse(order, CultureInfo.InvariantCulture) : int.MaxValue);
        }

        private static string? Value(IReadOnlyList<string> lines, string key) =>
            lines.FirstOrDefault(line => line.StartsWith(key, StringComparison.Ordinal)) is { } line ? Unquote(line[key.Length..].Trim()) : null;

        private static string Unquote(string value) =>
            value.Length >= 2 && value[0] == '"' && value[^1] == '"'
                ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace(@"\\", @"\", StringComparison.Ordinal)
                : value;
    }
}
