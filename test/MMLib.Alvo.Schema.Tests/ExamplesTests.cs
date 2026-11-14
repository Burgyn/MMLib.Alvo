using MMLib.Alvo.Docs.Tests;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

public class ExamplesTests
{
    private static readonly Dictionary<string, Expectation> _expectations = LoadExpectations();

    public static IEnumerable<object[]> Positive() =>
        SchemaPaths.Examples().Select(path => new object[] { path });

    public static IEnumerable<object[]> Negative() =>
        SchemaPaths.NegativeExamples().Where(path => !RefusedByApply(path)).Select(path => new object[] { path });

    /// <summary>The negative examples whose refusal is the apply's own cross-key check (<c>"refusedBy": "apply"</c>).</summary>
    public static IEnumerable<object[]> ApplyRefused() =>
        SchemaPaths.NegativeExamples().Where(RefusedByApply).Select(path => new object[] { path });

    public static IEnumerable<object[]> Snippets() =>
        DocsSnippets.All().Select(path => new object[] { path });

    [Fact]
    public void The_docs_snippets_tree_is_findable() =>
        DocsSnippets.All().ShouldNotBeEmpty("website/src/snippets must hold at least one *.alvo.json, or the theory below covers nothing");

    [Theory]
    [MemberData(nameof(Snippets))]
    public void Docs_snippet_validates(string path)
    {
        var failures = SchemaValidator.Failures(SchemaValidator.Load(), File.ReadAllText(path));
        failures.ShouldBeEmpty($"{DocsSnippets.Relative(path)} must validate against the schema");
    }

    [Theory]
    [MemberData(nameof(Positive))]
    public void Example_validates(string path)
    {
        var schema = SchemaValidator.Load();
        var failures = SchemaValidator.Failures(schema, File.ReadAllText(path));
        failures.ShouldBeEmpty($"{Path.GetFileName(path)} must validate against the schema");
    }

    [Theory]
    [MemberData(nameof(Negative))]
    public void Negative_example_is_rejected(string path)
    {
        string fileName = Path.GetFileName(path);
        var schema = SchemaValidator.Load();
        var failures = SchemaValidator.Failures(schema, File.ReadAllText(path));

        failures.ShouldNotBeEmpty($"{fileName} must be rejected by the schema");

        // Assert the expected pointer, not just non-emptiness, so a fixture
        // rejected for an unrelated reason cannot pass silently.
        _expectations.ShouldContainKey(
            fileName,
            $"add an expected pointer for {fileName} in examples/_negative/expectations.json");
        Expectation expected = _expectations[fileName];

        failures.ShouldContain(
            failure => failure.Pointer == expected.Pointer
                && (expected.MessageContains == null || failure.Message.Contains(expected.MessageContains)),
            $"{fileName} must be rejected at {expected.Pointer}"
                + (expected.MessageContains == null ? "" : $" mentioning '{expected.MessageContains}'"));
    }

    /// <summary>
    /// <b>An apply-refused example passes the schema</b>: its rule spans keys, and a schema conditional beside the apply's
    /// check would report the same fault twice, the second time without a fix (the <c>auth.signUp</c> lesson). The core
    /// suite's <c>ApplyRefusedNegativeExamplesTests</c> holds each to its pointer through the real validator.
    /// </summary>
    [Theory]
    [MemberData(nameof(ApplyRefused))]
    public void Apply_refused_negative_example_passes_the_schema(string path)
    {
        var failures = SchemaValidator.Failures(SchemaValidator.Load(), File.ReadAllText(path));

        failures.ShouldBeEmpty($"{Path.GetFileName(path)} is refused by the apply alone; the schema must hold no cross-key rule for it");
    }

    private static Dictionary<string, Expectation> LoadExpectations()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SchemaPaths.NegativeExpectationsFile));
        var map = new Dictionary<string, Expectation>(StringComparer.Ordinal);
        foreach (JsonProperty entry in document.RootElement.EnumerateObject())
        {
            string pointer = entry.Value.GetProperty("pointer").GetString()!;
            string? messageContains = entry.Value.TryGetProperty("messageContains", out JsonElement value)
                ? value.GetString()
                : null;
            string? refusedBy = entry.Value.TryGetProperty("refusedBy", out JsonElement stage)
                ? stage.GetString()
                : null;
            map[entry.Name] = new Expectation(pointer, messageContains, refusedBy);
        }

        return map;
    }

    private static bool RefusedByApply(string path) =>
        _expectations.TryGetValue(Path.GetFileName(path), out var expected) && expected.RefusedBy == "apply";

    private sealed record Expectation(string Pointer, string? MessageContains, string? RefusedBy);
}
