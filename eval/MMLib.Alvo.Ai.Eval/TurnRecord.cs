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
internal sealed record TurnRecord(
    string OriginalDescriptor, IReadOnlyList<AssistantUpdate> Updates, TimeSpan Elapsed, int Requests, int ToolRounds,
    long Tokens, IReadOnlyList<RecordedCall> Calls)
{
    private static readonly HashSet<string> _dryRuns = new(StringComparer.Ordinal) { "check_change", "propose_change" };

    internal AssistantUpdate.Proposal? Proposal => Updates.OfType<AssistantUpdate.Proposal>().LastOrDefault();

    internal bool HasValidProposal => Proposal is { Refusals.Count: 0 };

    internal string Answer => string.Concat(Updates.OfType<AssistantUpdate.Text>().Select(text => text.Delta));

    internal IReadOnlyList<string> ToolCalls => [.. Updates.OfType<AssistantUpdate.ToolInvoked>().Select(update => update.Tool)];

    internal int ProposeCalls => ToolCalls.Count(tool => tool == "propose_change");

    /// <summary>What every dry-run tool answered, parsed; an answer that is not a JSON object is left out.</summary>
    internal IReadOnlyList<JsonObject> Outcomes =>
        [.. Calls.Where(call => _dryRuns.Contains(call.Tool)).Select(call => ObjectOf(call.Result)).OfType<JsonObject>()];

    internal int RefusedAttempts =>
        Outcomes.Count(outcome => !(outcome["valid"] is JsonValue flag && flag.TryGetValue<bool>(out var valid) && valid));

    internal IReadOnlyList<string> ChangedPaths =>
        Proposal is { } proposal ? DescriptorDiff.Paths(OriginalDescriptor, proposal.DescriptorJson) : [];

    internal JsonNode? Proposed(string pointer) =>
        Proposal is { } proposal ? DescriptorDiff.At(JsonNode.Parse(proposal.DescriptorJson), pointer) : null;

    /// <summary>The proposed string at <paramref name="pointer"/>, or <see langword="null"/> when it is absent or not a string.</summary>
    internal string? ProposedText(string pointer) => TextOf(Proposed(pointer));

    internal bool HasViolation(string field, string value) =>
        Outcomes.SelectMany(outcome => outcome["violations"] as JsonArray ?? []).Any(violation => TextOf(violation?[field]) == value);

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
