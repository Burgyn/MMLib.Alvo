using MMLib.Alvo.Api;
using MMLib.Alvo.DocsGen.Problems;
using System.Text.Json;

namespace MMLib.Alvo.DocsGen.Tests.Problems;

public class ProblemTypeNotesTests
{
    private static readonly string _root = RepositoryRoot.Find();
    private static readonly IReadOnlyDictionary<string, ProblemTypeNote> _notes = ProblemTypeNotes.Load(Path.Combine(_root, "website"));

    [Fact]
    public void Every_slug_has_notes_and_every_note_is_a_slug() =>
        _notes.Keys.Order(StringComparer.Ordinal).ShouldBe(AlvoProblemTypes.All.Order(StringComparer.Ordinal));

    [Fact]
    public void Only_the_opt_in_slugs_need_problem_details() =>
        _notes.Where(n => n.Value.NeedsProblemDetails).Select(n => n.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["function-failed", "internal", "unreadable-request"]);

    [Fact]
    public void Every_note_has_a_cause_a_fix_and_a_guide() =>
        _notes.Values.ShouldAllBe(note => note.Causes.Count > 0 && note.Fix.Length > 0 && note.Guides.Count > 0);

    [Fact]
    public void Every_violation_code_is_emitted_by_the_source()
    {
        var source = string.Concat(Directory.EnumerateFiles(Path.Combine(_root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        _notes.SelectMany(n => n.Value.ViolationCodes).Distinct().ShouldAllBe(code => source.Contains($"\"{code}\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_guide_link_is_a_page()
    {
        var docs = Path.Combine(_root, "website", "src", "content", "docs");

        _notes.SelectMany(n => n.Value.Guides).ShouldAllBe(slug =>
            File.Exists(Path.Combine(docs, slug + ".mdx")) || File.Exists(Path.Combine(docs, slug + ".md")));
    }

    [Fact]
    public void An_unknown_member_is_refused() =>
        Should.Throw<JsonException>(() => ProblemTypeNotes.Parse(
            """{ "validation": { "causes": [], "fix": "", "violationCodes": [], "guides": [], "needsProblemDetails": false, "typo": 1 } }"""));

    [Fact]
    public void A_missing_member_is_refused() =>
        Should.Throw<JsonException>(() => ProblemTypeNotes.Parse("""{ "validation": { "causes": [], "fix": "" } }"""));
}
