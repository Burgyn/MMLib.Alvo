using MMLib.Alvo.Api;
using MMLib.Alvo.DocsGen.Markdown;
using MMLib.Alvo.DocsGen.Schema;
using MMLib.Alvo.DocsGen.Xml;
using System.Text;

namespace MMLib.Alvo.DocsGen.Problems;

internal sealed class ProblemTypesGenerator : IPageGenerator
{
    private const string EveryHost = "every host";
    private const string OptInHosts = "the standalone host; an embedded host only with `AddAlvoProblemDetails()`";
    private const string NoCodes = "none — this refusal carries no itemised reasons";

    public Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        var types = ProblemTypeCatalog.Read(XmlDocs.Load([typeof(AlvoProblemTypes).Assembly]));
        var notes = ProblemTypeNotes.Load(context.Paths.SiteRoot);
        var titles = new GuideTitles(context.Paths.DocsDir);
        return Task.FromResult<IReadOnlyList<GeneratedPage>>([Render(types, notes, titles.TitleOf)]);
    }

    internal static GeneratedPage Render(IReadOnlyList<ProblemType> types, IReadOnlyDictionary<string, ProblemTypeNote> notes, Func<string, string> guideTitle)
    {
        var page = new StringBuilder(Md.Frontmatter(
                "Problem types", "Every RFC 9457 problem type the API can answer with, generated from AlvoProblemTypes.", 3, tocMaxHeadingLevel: 2))
            .Append("Every refusal is an RFC 9457 problem document whose `type` is one of the URIs below. ")
            .Append("Branch on the slug, never on `detail`, which is prose.\n\n")
            .Append("The `type` URIs use the namespace `https://alvo.dev/errors/`, which does not resolve yet. ")
            .Append("The slug after the last `/` is the anchor on this page: `https://alvo.dev/errors/forbidden` is [`#forbidden`](#forbidden).\n");
        foreach (var type in types)
        {
            AppendSection(page, type, NoteFor(notes, type.Slug), guideTitle);
        }

        return new GeneratedPage(OutputRoot.Reference, "problem-types.md", page.ToString());
    }

    private static ProblemTypeNote NoteFor(IReadOnlyDictionary<string, ProblemTypeNote> notes, string slug) =>
        notes.TryGetValue(slug, out var note)
            ? note
            : throw new InvalidOperationException($"website/src/data/problem-types.json has no entry for the problem type '{slug}'.");

    private static void AppendSection(StringBuilder page, ProblemType type, ProblemTypeNote note, Func<string, string> guideTitle)
    {
        page.Append("\n## ").Append(Md.Code(type.Slug)).Append("\n\n")
            .Append("**Status:** ").Append(type.StatusLabel)
            .Append(" · **`type`:** ").Append(Md.Code(type.Uri))
            .Append(" · **Returned by:** ").Append(note.NeedsProblemDetails ? OptInHosts : EveryHost).Append("\n\n")
            .Append(type.Meaning).Append("\n\n")
            .Append("**Causes**\n\n");
        foreach (var cause in note.Causes)
        {
            page.Append("- ").Append(Md.Text(cause)).Append('\n');
        }

        page.Append("\n**Fix:** ").Append(Md.Text(note.Fix)).Append("\n\n")
            .Append("**Violation codes:** ").Append(Codes(note.ViolationCodes)).Append("\n\n")
            .Append("**Guides:** ").Append(Guides(note.Guides, guideTitle)).Append('\n');
    }

    private static string Codes(IReadOnlyList<string> codes) =>
        codes.Count == 0 ? NoCodes : string.Join(", ", codes.Select(Md.Code));

    private static string Guides(IReadOnlyList<string> guides, Func<string, string> guideTitle) =>
        string.Join(" · ", guides.Select(guide => $"[{Md.Text(guideTitle(guide))}]({SiteLinks.Page(guide)})"));
}
