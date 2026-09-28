using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

using System.Text.Json;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>Every refusal the dry-run pipeline can meet, as the one <see cref="ToolViolation"/> shape.</summary>
/// <remarks>
/// A violation names the operation that caused it by pointer: the validator reports at RFC 6901 pointers of the same
/// grammar a patch writes, so <see cref="JsonPointer"/> is the one parser for both.
/// </remarks>
internal static class ViolationMapping
{
    internal const string StaleRevisionCode = "stale-revision";
    internal const string AttemptsExhaustedCode = "attempts-exhausted";
    internal const string AccessReservedCode = "access-reserved";
    private const string AccessPointer = "/access";
    private const string EntitiesToken = "entities";
    private const string FieldsToken = "fields";
    private const string RebaseFix = "Call get_descriptor again and write the operations against its revision.";

    internal static ToolViolation FromPatch(JsonPatchError error) =>
        new(ToolViolation.Patch, error.Pointer, error.Message, error.Fix, error.Op, error.Code);

    internal static ToolViolation Stale(int current, int based) => new(
        ToolViolation.Concurrency, string.Empty,
        $"The descriptor is at revision {current}; this change was written against revision {based}.",
        RebaseFix, Code: StaleRevisionCode);

    internal static ToolViolation FromConcurrency(DescriptorConcurrencyException stale) =>
        new(ToolViolation.Concurrency, string.Empty, stale.Message, RebaseFix, Code: StaleRevisionCode);

    internal static ToolViolation FromEscalation(ManagementEscalationException escalation) => new(
        ToolViolation.Access, AccessPointer, escalation.Message,
        "Leave /access unchanged; an administrator has to make that change.", Code: AccessReservedCode);

    internal static ToolViolation BudgetSpent() => new(
        ToolViolation.Budget, string.Empty,
        "Stop proposing. Explain to the operator what the framework refused, quoting it.", Fix: null,
        Code: AttemptsExhaustedCode);

    internal static IReadOnlyList<ToolViolation> FromValidation(DescriptorValidationException refused, JsonElement operations) =>
    [
        .. refused.Result.Errors.Select(error => new ToolViolation(
            ToolViolation.Validation, error.Path, error.Message, error.FixSuggestion, OpFor(operations, error.Path),
            Severity: SeverityOf(error.Severity))),
    ];

    internal static ToolViolation FromDestructive(DestructiveChangeNotAllowedException refused, JsonElement operations)
    {
        var pointer = DestructivePointer(refused.Plan);
        return new ToolViolation(
            ToolViolation.Plan, pointer, refused.Message,
            "Only the operator can allow a destructive change, from Preview. Say first what data it loses.",
            OpFor(operations, pointer));
    }

    /// <summary>The operation whose path is the pointer's prefix, or lies under it — the deepest such, the last on a tie.</summary>
    internal static int? OpFor(JsonElement operations, string pointer)
    {
        if (!JsonPointer.TryParse(pointer, out var reported) || reported.IsRoot || operations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int? best = null;
        var bestDepth = -1;
        var index = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            if (PathOf(operation) is { } path && Related(path, reported) && path.Tokens.Count >= bestDepth)
            {
                (best, bestDepth) = (index, path.Tokens.Count);
            }

            index++;
        }

        return best;
    }

    private static string SeverityOf(DescriptorValidationSeverity severity) =>
        severity == DescriptorValidationSeverity.Error ? ToolViolation.ErrorSeverity : ToolViolation.WarningSeverity;

    private static string DestructivePointer(MigrationPlan plan) =>
        plan.Steps.FirstOrDefault(step => step.IsDestructive)?.Change is { } change
            ? PointerTo(change.Entity, change.Field).Text
            : string.Empty;

    private static JsonPointer PointerTo(string entity, string? field)
    {
        var toEntity = JsonPointer.Root.Append(EntitiesToken).Append(entity);
        return field is null ? toEntity : toEntity.Append(FieldsToken).Append(field);
    }

    private static JsonPointer? PathOf(JsonElement operation) =>
        operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("path", out var path)
        && path.ValueKind == JsonValueKind.String && JsonPointer.TryParse(path.GetString(), out var parsed)
            ? parsed
            : null;

    private static bool Related(JsonPointer path, JsonPointer reported) =>
        !path.IsRoot
        && (path.Tokens.SequenceEqual(reported.Tokens, StringComparer.Ordinal)
            || path.IsProperPrefixOf(reported)
            || reported.IsProperPrefixOf(path));
}
