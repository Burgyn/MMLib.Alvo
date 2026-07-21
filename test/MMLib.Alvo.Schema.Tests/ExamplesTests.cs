using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

public class ExamplesTests
{
    private static readonly Dictionary<string, Expectation> _expectations = LoadExpectations();

    public static IEnumerable<object[]> Positive() =>
        SchemaPaths.Examples().Select(path => new object[] { path });

    public static IEnumerable<object[]> Negative() =>
        SchemaPaths.NegativeExamples().Select(path => new object[] { path });

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
            map[entry.Name] = new Expectation(pointer, messageContains);
        }

        return map;
    }

    private sealed record Expectation(string Pointer, string? MessageContains);
}
