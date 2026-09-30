using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>Every refusal the dry-run pipeline can meet, as the one <see cref="ToolViolation"/> shape.</summary>
/// <remarks>
/// <para>
/// A violation names the operation that caused it by pointer: the validator reports at RFC 6901 pointers against the
/// patched document, so an operation is matched by where it <em>landed</em> (<see cref="JsonPatchResult.Targets"/>) —
/// an append as its real index, after every shift an earlier operation made — never by the path it was written with.
/// </para>
/// <para>
/// <b>Related both ways (D14).</b> An operation matches when its target is the reported pointer, contains it, or lies
/// under it; the deepest wins, the last on a tie. A validator that reports at a container (<c>/entities/bikes</c>)
/// about a field an operation added beneath it still names that operation.
/// </para>
/// </remarks>
internal static class ViolationMapping
{
    internal const string StaleRevisionCode = "stale-revision";
    internal const string AttemptsExhaustedCode = "attempts-exhausted";
    internal const string AccessReservedCode = "access-reserved";

    /// <summary>How the budget answer begins: the attempt was not checked, so it must never be reported as refused.</summary>
    internal const string UncheckedLead = "This attempt was not checked: the turn's refusal budget is spent.";

    /// <summary>How the budget answer quotes a re-sent patch's own earlier refusal.</summary>
    internal const string OwnRefusalLead = "This exact patch was refused earlier:";

    /// <summary>What the budget answer says when a valid proposal is already filed this turn.</summary>
    internal const string ProposalFiledNote = "A valid proposal is already filed for the operator to review";

    /// <summary>What the budget answer says when the refusal it would quote carried no blocking violation.</summary>
    internal const string NothingToQuoteNote = "The last refused attempt carried no blocking violation to quote.";
    private const string AccessPointer = "/access";
    private const string EntitiesToken = "entities";
    private const string FieldsToken = "fields";
    private const string RebaseFix = "Call get_descriptor again and write the operations against its revision.";

    /// <summary>What an outcome says when a violation names a skill the turn has not loaded (D50).</summary>
    internal const string SkillHint =
        "Load the skill a violation names with load_skill before you retry: it states the rule that violation is about.";

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

    /// <summary>The budget answer: this attempt was not checked, what it quotes, and what to tell the operator (D41).</summary>
    /// <param name="refusals">The refusals to quote, verbatim: the patch's own when it was refused before, else the last.</param>
    /// <param name="ownRefusal">Whether <paramref name="refusals"/> are this exact patch's own earlier refusal.</param>
    /// <param name="proposalFiled">Whether a valid proposal is already filed this turn.</param>
    internal static ToolViolation BudgetSpent(IReadOnlyList<string> refusals, bool ownRefusal, bool proposalFiled) => new(
        ToolViolation.Budget, string.Empty,
        $"{UncheckedLead} {Quoted(refusals, ownRefusal)} {Closing(proposalFiled)}",
        Fix: null, Code: AttemptsExhaustedCode);

    /// <summary>The refusal the budget answer carries, ending in one full stop — or the note that there is none.</summary>
    private static string Quoted(IReadOnlyList<string> refusals, bool ownRefusal)
    {
        if (refusals.Count == 0)
        {
            return NothingToQuoteNote;
        }

        var quoted = string.Join(" | ", refusals);
        return $"{(ownRefusal ? OwnRefusalLead : "The last refused attempt said:")} {quoted}{(quoted.EndsWith('.') ? string.Empty : ".")}";
    }

    private static string Closing(bool proposalFiled) => proposalFiled
        ? $"{ProposalFiledNote}; tell the operator about it, and never call this attempt refused."
        : "Quote that refusal to the operator; never call this attempt refused.";

    internal static IReadOnlyList<ToolViolation> FromValidation(DescriptorValidationException refused, IReadOnlyList<string?> targets) =>
    [
        .. refused.Result.Errors.Select(error => new ToolViolation(
            ToolViolation.Validation, error.Path, error.Message, error.FixSuggestion, OpFor(targets, error.Path),
            Severity: SeverityOf(error.Severity))),
    ];

    /// <summary>A valid dry run's warnings (D52), mapped as a refusal's violations are, each a <c>warning</c>.</summary>
    internal static IReadOnlyList<ToolViolation> FromWarnings(IReadOnlyList<DescriptorValidationError> warnings, IReadOnlyList<string?> targets) =>
    [
        .. warnings.Select(warning => new ToolViolation(
            ToolViolation.Validation, warning.Path, warning.Message, warning.FixSuggestion, OpFor(targets, warning.Path),
            Severity: ToolViolation.WarningSeverity)),
    ];

    internal static ToolViolation FromDestructive(DestructiveChangeNotAllowedException refused, IReadOnlyList<string?> targets)
    {
        var pointer = DestructivePointer(refused.Plan);
        return new ToolViolation(
            ToolViolation.Plan, pointer, refused.Message,
            "Only the operator can allow a destructive change, from Preview. Say first what data it loses.",
            OpFor(targets, pointer));
    }

    /// <summary>The operation whose target is the pointer's prefix, or lies under it — the deepest such, the last on a tie.</summary>
    /// <param name="targets">Where each operation landed, index-aligned with the patch.</param>
    /// <param name="pointer">The pointer the violation was reported at.</param>
    internal static int? OpFor(IReadOnlyList<string?> targets, string pointer)
    {
        if (!JsonPointer.TryParse(pointer, out var reported) || reported.IsRoot)
        {
            return null;
        }

        int? best = null;
        var bestDepth = -1;
        for (var index = 0; index < targets.Count; index++)
        {
            if (JsonPointer.TryParse(targets[index], out var target) && Related(target, reported) && target.Tokens.Count >= bestDepth)
            {
                (best, bestDepth) = (index, target.Tokens.Count);
            }
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

    private static bool Related(JsonPointer path, JsonPointer reported) =>
        !path.IsRoot
        && (path.Tokens.SequenceEqual(reported.Tokens, StringComparer.Ordinal)
            || path.IsProperPrefixOf(reported)
            || reported.IsProperPrefixOf(path));
}
