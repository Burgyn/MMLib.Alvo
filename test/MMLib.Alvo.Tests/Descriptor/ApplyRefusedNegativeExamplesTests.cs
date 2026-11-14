using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// The negative examples the schema accepts and the apply refuses (<c>"refusedBy": "apply"</c> in
/// <c>examples/_negative/expectations.json</c>) are refused by the real validator, at their expected pointer.
/// </summary>
/// <remarks>
/// The schema corpus (<c>ExamplesTests</c>) proves the other half — that the schema accepts them, so a cross-key fault
/// is reported once, by the check that carries its fix. Neither suite alone pins both.
/// </remarks>
public sealed class ApplyRefusedNegativeExamplesTests
{
    private static string NegativeDirectory => Path.Combine(RepositoryRoot.Find(), "examples", "_negative");

    /// <summary>Every apply-refused example, with its expected pointer and message fragment.</summary>
    public static TheoryData<string, string, string?> ApplyRefused()
    {
        using var expectations = JsonDocument.Parse(File.ReadAllText(Path.Combine(NegativeDirectory, "expectations.json")));
        var data = new TheoryData<string, string, string?>();
        foreach (var entry in expectations.RootElement.EnumerateObject().Where(IsApplyRefused))
        {
            data.Add(
                entry.Name,
                entry.Value.GetProperty("pointer").GetString()!,
                entry.Value.TryGetProperty("messageContains", out var fragment) ? fragment.GetString() : null);
        }

        return data;
    }

    [Fact]
    public void The_corpus_holds_apply_refused_examples() =>
        ApplyRefused().ShouldNotBeEmpty("the theory below covers nothing without one");

    [Theory]
    [MemberData(nameof(ApplyRefused))]
    public void An_apply_refused_example_is_refused_at_its_pointer(string file, string path, string? messageContains)
    {
        var result = new DescriptorValidator().Validate(File.ReadAllText(Path.Combine(NegativeDirectory, file)));

        result.Errors.ShouldContain(
            error => error.Severity == DescriptorValidationSeverity.Error
                && error.Path == path
                && (messageContains == null || error.Message.Contains(messageContains, StringComparison.Ordinal))
                && error.FixSuggestion != null,
            $"{file} must be refused at {path}, with a fix");
    }

    private static bool IsApplyRefused(JsonProperty entry) =>
        entry.Value.TryGetProperty("refusedBy", out var stage) && stage.GetString() == "apply";
}
