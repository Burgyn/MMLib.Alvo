using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The worked examples in the assistant's instructions, as data — so both suites can run them and none can rot.
/// </summary>
/// <remarks>
/// An example is a <c>&lt;!-- example: name --&gt;</c> marker followed by two <c>json</c> fences: the call, then the
/// outcome it claims. The outcome is abbreviated to what the example teaches — <c>valid</c>, <c>changedPaths</c>, and
/// for a refusal the violation's <c>source</c> and a <em>fragment</em> of its message, which the validator's own
/// message must contain.
/// </remarks>
internal static partial class InstructionExamples
{
    internal static IReadOnlyList<InstructionExample> Parse(string markdown) =>
    [
        .. Example().Matches(markdown).Select(match => new InstructionExample(
            match.Groups["name"].Value, Json(match.Groups["call"].Value), Json(match.Groups["outcome"].Value))),
    ];

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [GeneratedRegex(
        @"<!-- example: (?<name>[a-z0-9-]+) -->.*?```json\s*(?<call>.*?)```.*?```json\s*(?<outcome>.*?)```",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Example();
}

/// <summary>One worked example: what the model sends and what the instructions claim comes back.</summary>
internal sealed record InstructionExample(string Name, JsonElement Call, JsonElement Outcome)
{
    internal string Tool => Call.GetProperty("tool").GetString()!;

    internal JsonElement Operations => Call.GetProperty("operations");

    internal bool ClaimsValid => Outcome.GetProperty("valid").GetBoolean();

    internal IReadOnlyList<string> ChangedPaths =>
        Outcome.TryGetProperty("changedPaths", out var paths) ? [.. paths.EnumerateArray().Select(path => path.GetString()!)] : [];

    /// <summary>Each claimed violation: the stage that says it, and a fragment its message contains.</summary>
    internal IReadOnlyList<(string Source, string Message)> ClaimedViolations =>
        Outcome.TryGetProperty("violations", out var violations)
            ? [.. violations.EnumerateArray().Select(v => (v.GetProperty("source").GetString()!, v.GetProperty("message").GetString()!))]
            : [];

    public override string ToString() => Name;
}
