using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Everything one turn produced that a case grades.</summary>
/// <param name="OriginalDescriptor">The applied descriptor the turn started from.</param>
/// <param name="Updates">What the assistant streamed, in order.</param>
/// <param name="Elapsed">The turn's wall-clock time.</param>
/// <param name="Requests">How many times the loop asked the model.</param>
/// <param name="ToolRounds">How many of the model's answers asked for a tool — what the iteration cap bounds.</param>
/// <param name="Tokens">The tokens the provider reported.</param>
/// <param name="Calls">Every tool call, with the tool's answer.</param>
/// <param name="ProviderStatus">
/// The provider's HTTP status when a turn failed on an answer it received, <c>none</c> when it failed on no answer at
/// all, <see langword="null"/> when nothing failed — the status only, never the provider's message, which can echo the
/// request.
/// </param>
/// <param name="FollowUps">How many follow-ups the harness sent the model this turn (D47): 0 or 1.</param>
internal sealed record TurnRecord(
    string OriginalDescriptor, IReadOnlyList<AssistantUpdate> Updates, TimeSpan Elapsed, int Requests, int ToolRounds,
    long Tokens, IReadOnlyList<RecordedCall> Calls, string? ProviderStatus = null, int FollowUps = 0)
{
    private static readonly HashSet<string> _dryRuns = new(StringComparer.Ordinal) { "check_change", "propose_change" };

    internal AssistantUpdate.Proposal? Proposal => Updates.OfType<AssistantUpdate.Proposal>().LastOrDefault();

    internal bool HasValidProposal => Proposal is { Refusals.Count: 0 };

    internal string Answer => string.Concat(Updates.OfType<AssistantUpdate.Text>().Select(text => text.Delta));

    internal IReadOnlyList<string> ToolCalls => [.. Updates.OfType<AssistantUpdate.ToolInvoked>().Select(update => update.Tool)];

    /// <summary>The management tools the turn called — what the ≤ 6 bar counts (D34).</summary>
    internal int ManagementCalls => ToolCalls.Count(tool => !SkillsRead.SkillTools.Contains(tool));

    /// <summary>The skill loads and resource reads the turn made, bounded apart (D34).</summary>
    internal int SkillReads => ToolCalls.Count(SkillsRead.SkillTools.Contains);

    internal int ProposeCalls => ToolCalls.Count(tool => tool == "propose_change");

    /// <summary>What every dry-run tool answered, parsed; an answer that is not a JSON object is left out.</summary>
    internal IReadOnlyList<JsonObject> Outcomes =>
        [.. Calls.Where(call => _dryRuns.Contains(call.Tool)).Select(call => ObjectOf(call.Result)).OfType<JsonObject>()];

    /// <summary>The dry runs that were refused.</summary>
    /// <remarks>An <c>unchecked</c> budget answer was no dry run (D41), so it is not counted.</remarks>
    internal int RefusedAttempts =>
        Outcomes.Where(outcome => outcome["unchecked"] is null).Count(outcome => !(outcome["valid"] is JsonValue flag && flag.TryGetValue<bool>(out var valid) && valid));

    internal IReadOnlyList<string> ChangedPaths =>
        Proposal is { } proposal ? DescriptorDiff.Paths(OriginalDescriptor, proposal.DescriptorJson) : [];

    internal JsonNode? Proposed(string pointer) =>
        Proposal is { } proposal ? DescriptorDiff.At(JsonNode.Parse(proposal.DescriptorJson), pointer) : null;

    /// <summary>The proposed string at <paramref name="pointer"/>, or <see langword="null"/> when it is absent or not a string.</summary>
    internal string? ProposedText(string pointer) => TextOf(Proposed(pointer));

    internal bool HasViolation(string field, string value) =>
        Outcomes.SelectMany(outcome => outcome["violations"] as JsonArray ?? []).Any(violation => TextOf(violation?[field]) == value);

    /// <summary>Every <c>message</c> and <c>fix</c> the dry runs returned — the framework's words, not the model's.</summary>
    internal IReadOnlyList<string> FrameworkTexts =>
    [
        .. Outcomes.SelectMany(outcome => outcome["violations"] as JsonArray ?? [])
            .SelectMany(violation => new[] { TextOf(violation?["message"]), TextOf(violation?["fix"]) })
            .OfType<string>()
            .Where(text => text.Length > 0),
    ];

    /// <summary>Whether any dry run's plan said it would destroy data.</summary>
    internal bool AnyDestructivePlan =>
        Outcomes.Any(outcome => outcome["plan"]?["hasDestructiveChanges"] is JsonValue flag && flag.TryGetValue<bool>(out var destructive) && destructive);

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject? ObjectOf(string? result)
    {
        try
        {
            return result is null ? null : JsonNode.Parse(result) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
