using MMLib.Alvo.Management;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>What <c>check_change</c> and <c>propose_change</c> tell the model — one shape for every result.</summary>
/// <param name="Valid">Whether the change would apply.</param>
/// <param name="Revision">The revision the descriptor is at — the one to re-base on after a stale refusal.</param>
/// <param name="Plan">The migration plan the dry run produced, or the destructive plan it refused.</param>
/// <param name="ChangedPaths">What the patch touched, computed by Alvo rather than claimed by the model.</param>
/// <param name="Violations">Every refusal and warning, each at the pointer it concerns.</param>
/// <param name="AttemptsLeft">How many more refused attempts this turn may make.</param>
internal sealed record ChangeOutcome(
    bool Valid,
    int Revision,
    ManagementPlanSummary? Plan,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<ToolViolation> Violations,
    int AttemptsLeft)
{
    internal static ChangeOutcome From(DraftAttempt attempt, int attemptsLeft) =>
        new(attempt.Valid, attempt.Revision, attempt.Plan, attempt.ChangedPaths, attempt.Violations, attemptsLeft);

    /// <summary>The answer once the refusal budget is spent: only the instruction to stop.</summary>
    /// <param name="revision">The revision the descriptor was last read at — never the model's claimed base.</param>
    internal static ChangeOutcome BudgetSpent(int revision) =>
        new(Valid: false, revision, Plan: null, [], [ViolationMapping.BudgetSpent()], AttemptsLeft: 0);
}

/// <summary>One thing the framework — or the tool — refused or warned about, at the pointer it concerns.</summary>
/// <param name="Source">Which stage said it: <c>patch</c>, <c>validation</c>, <c>plan</c>, <c>concurrency</c>, <c>access</c> or <c>budget</c>.</param>
/// <param name="Pointer">The RFC 6901 pointer it concerns; empty for the whole change.</param>
/// <param name="Message">The framework's words, verbatim.</param>
/// <param name="Fix">The framework's fix suggestion, verbatim, when it gave one.</param>
/// <param name="Op">The index of the operation whose path the pointer falls under, when one does.</param>
/// <param name="Code">A stable slug to branch on, where the stage has one.</param>
/// <param name="Severity"><c>error</c> blocks the change; <c>warning</c> does not.</param>
internal sealed record ToolViolation(
    string Source,
    string Pointer,
    string Message,
    string? Fix,
    int? Op = null,
    string? Code = null,
    string Severity = ToolViolation.ErrorSeverity)
{
    internal const string Patch = "patch";
    internal const string Validation = "validation";
    internal const string Plan = "plan";
    internal const string Concurrency = "concurrency";
    internal const string Access = "access";
    internal const string Budget = "budget";
    internal const string ErrorSeverity = "error";
    internal const string WarningSeverity = "warning";

    /// <summary>Whether this stops the change.</summary>
    internal bool Blocks => Severity == ErrorSeverity;

    /// <summary>The string <see cref="AssistantUpdate.Proposal.Refusals"/> has always carried.</summary>
    internal string AsRefusal() =>
        (Pointer.Length == 0 ? Message : $"{Pointer}: {Message}") + (Fix is null ? string.Empty : $" — {Fix}");
}
