using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Markdown;
using MMLib.Alvo.DocsGen.Xml;
using System.Text;

namespace MMLib.Alvo.DocsGen.Limits;

internal sealed class LimitsGenerator : IPageGenerator
{
    public Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GeneratedPage>>([Render(LimitsCatalog.Read(XmlDocs.Load(ShippedAssemblies.All)))]);

    internal static GeneratedPage Render(IReadOnlyList<Limit> limits)
    {
        var page = new StringBuilder(Md.Frontmatter("Limits and budgets", "Every size, depth and count limit the API enforces, read from the code.", 9))
            .Append("Each value below is read from the member that enforces it. A limit with a key can be changed in configuration; the others are fixed.\n\n")
            .Append("| Limit | Value | Configure with | What happens |\n|---|---|---|---|\n");
        foreach (var limit in limits)
        {
            page.Append(Row(limit));
        }

        page.Append("\nSee [Configuration keys](").Append(SiteLinks.Page("reference/configuration"))
            .Append(") for every key, and [Problem types](").Append(SiteLinks.Page("reference/problem-types"))
            .Append(") for the refusal a request over a limit receives.\n");
        return new GeneratedPage(OutputRoot.Reference, "limits.md", page.ToString());
    }

    internal static string Row(Limit limit) =>
        $"| {limit.Name} | {Md.Cell(limit.Value)} | {(limit.ConfigurationKey is { } key ? Md.CodeCell(key) : "—")} | {Md.MarkdownCell(limit.Description)} |\n";
}
