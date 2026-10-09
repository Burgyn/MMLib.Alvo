using MMLib.Alvo.Api;
using MMLib.Alvo.DocsGen.Problems;
using System.Text.Json;

namespace MMLib.Alvo.DocsGen.Tests.Problems;

public class ProblemTypeNotesTests
{
    private static readonly string _root = RepositoryRoot.Find();
    private static readonly IReadOnlyDictionary<string, ProblemTypeNote> _notes = ProblemTypeNotes.Load(Path.Combine(_root, "website"));

    private static readonly string _api = Path.Combine("MMLib.Alvo", "Api", "Internal");

    private static readonly Dictionary<string, string[][]> _producers = new(StringComparer.Ordinal)
    {
        ["validation"] =
        [
            [_api, "PayloadViolations.cs"], [_api, "BatchViolations.cs"], [_api, "BoundedJsonBody.cs"], [_api, "ProblemResultFactory.cs"],
        ],
        ["malformed-query"] = [[_api, "QueryViolations.cs"], [_api, "BoundedJsonBody.cs"]],
        ["forbidden"] = [["MMLib.Alvo.Data.EntityFrameworkCore", "Internal", "EfAlvoData.cs"]],
        ["conflict"] = [[_api, "ProblemResultFactory.cs"]],
    };

    [Fact]
    public void Every_slug_has_notes_and_every_note_is_a_slug() =>
        _notes.Keys.Order(StringComparer.Ordinal).ShouldBe(AlvoProblemTypes.All.Order(StringComparer.Ordinal));

    [Fact]
    public void Only_the_opt_in_slugs_need_problem_details() =>
        _notes.Where(n => n.Value.NeedsProblemDetails).Select(n => n.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["function-failed", "internal", "unreadable-request"]);

    [Fact]
    public void Only_the_management_slugs_are_management_api_only() =>
        _notes.Where(n => n.Value.ManagementApiOnly).Select(n => n.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["destructive-change", "precondition-required"]);

    [Fact]
    public void A_management_only_slug_is_produced_only_by_management_endpoints()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(_root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(path => path, File.ReadAllText);

        foreach (var slug in _notes.Where(n => n.Value.ManagementApiOnly).Select(n => n.Key))
        {
            var call = $"ProblemResultFactory.{PascalCase(slug)}(";
            var callers = sources.Where(source => source.Value.Contains(call, StringComparison.Ordinal)).Select(source => source.Key).ToList();

            callers.ShouldNotBeEmpty();
            callers.ShouldAllBe(path => path.Contains($"{Path.DirectorySeparatorChar}Management{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_note_has_a_cause_a_fix_and_a_guide() =>
        _notes.Values.ShouldAllBe(note => note.Causes.Count > 0 && note.Fix.Length > 0 && note.Guides.Count > 0);

    [Fact]
    public void Every_violation_code_is_emitted_by_the_source() =>
        _notes.SelectMany(n => n.Value.ViolationCodes).Distinct().ShouldAllBe(code => ProductSource.Emits(ProductSource.All, code));

    [Fact]
    public void Every_slug_with_codes_has_its_producers_listed() =>
        _notes.Where(n => n.Value.ViolationCodes.Count > 0).Select(n => n.Key).Order(StringComparer.Ordinal)
            .ShouldBe(_producers.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Every_violation_code_is_emitted_where_its_slug_is_produced()
    {
        foreach (var (slug, files) in _producers)
        {
            var source = string.Join('\n', files.Select(file => ProductSource.Read(file)));
            _notes[slug].ViolationCodes.ShouldAllBe(code => ProductSource.Emits(source, code), $"slug '{slug}'");
        }
    }

    [Fact]
    public void Both_body_paths_publish_the_shared_bound_codes()
    {
        ProductSource.Read(_api, "PayloadViolations.cs").ShouldContain("BoundedJsonBody.CodeOf(");
        ProductSource.Read(_api, "QueryViolations.cs").ShouldContain("BoundedJsonBody.CodeOf(");
        ProductSource.Read(_api, "QueryBodyReader.cs").ShouldContain("QueryViolations.Body(");
        ProductSource.Read(_api, "DataApiEndpoints.cs").ShouldContain("ProblemResultFactory.MalformedQuery(body.Violations)");
    }

    [Fact]
    public void Every_guide_link_is_a_page()
    {
        var docs = Path.Combine(_root, "website", "src", "content", "docs");

        _notes.SelectMany(n => n.Value.Guides).ShouldAllBe(slug =>
            File.Exists(Path.Combine(docs, slug + ".mdx")) || File.Exists(Path.Combine(docs, slug + ".md")));
    }

    private static string PascalCase(string slug) =>
        string.Concat(slug.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    [Fact]
    public void An_unknown_member_is_refused() =>
        Should.Throw<JsonException>(() => ProblemTypeNotes.Parse(
            """{ "validation": { "causes": [], "fix": "", "violationCodes": [], "guides": [], "needsProblemDetails": false, "typo": 1 } }"""));

    [Fact]
    public void A_missing_member_is_refused() =>
        Should.Throw<JsonException>(() => ProblemTypeNotes.Parse("""{ "validation": { "causes": [], "fix": "" } }"""));
}
