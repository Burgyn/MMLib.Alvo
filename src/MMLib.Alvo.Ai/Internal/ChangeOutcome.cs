using MMLib.Alvo.Management;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>What <c>check_change</c> and <c>propose_change</c> tell the model — one shape for every result.</summary>
/// <param name="Valid">Whether the change would apply.</param>
/// <param name="Revision">The revision the descriptor is at — the one to re-base on after a stale refusal.</param>
/// <param name="Plan">The migration plan the dry run produced, or the destructive plan it refused.</param>
/// <param name="ChangedPaths">What the patch touched, computed by Alvo rather than claimed by the model.</param>
/// <param name="Violations">Every refusal and warning, each at the pointer it concerns.</param>
/// <param name="AttemptsLeft">
/// How many more refusals that make no progress this turn may make — or fewer, when the turn's ceiling on refusals is
/// nearer (D41).
/// </param>
/// <param name="Unchecked">
/// <see langword="true"/> only on the budget answer: this attempt was not dry-run, so nothing about it is known.
/// </param>
/// <param name="Hint">
/// <see cref="ViolationMapping.SkillHint"/> when a blocking violation names a skill the turn has not loaded (D50);
/// otherwise absent.
/// </param>
internal sealed record ChangeOutcome(
    bool Valid,
    int Revision,
    ManagementPlanSummary? Plan,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<ToolViolation> Violations,
    int AttemptsLeft,
    bool? Unchecked = null,
    string? Hint = null)
{
    internal static ChangeOutcome From(DraftAttempt attempt, int attemptsLeft) =>
        new(attempt.Valid, attempt.CurrentRevision, attempt.Plan, attempt.ChangedPaths, attempt.Violations, attemptsLeft);

    /// <summary>The answer once the refusal budget is spent: not dry-run, and saying so (D41).</summary>
    /// <param name="revision">The revision the descriptor was last read at — never the model's claimed base.</param>
    /// <param name="budget">The budget violation, as <see cref="ViolationMapping.BudgetSpent"/> words it.</param>
    internal static ChangeOutcome BudgetSpent(int revision, ToolViolation budget) =>
        new(Valid: false, revision, Plan: null, [], [budget], AttemptsLeft: 0, Unchecked: true);
}

/// <summary>One thing the framework — or the tool — refused or warned about, at the pointer it concerns.</summary>
/// <param name="Source">Which stage said it: <c>patch</c>, <c>validation</c>, <c>plan</c>, <c>concurrency</c>, <c>access</c> or <c>budget</c>.</param>
/// <param name="Pointer">The RFC 6901 pointer it concerns; empty for the whole change.</param>
/// <param name="Message">The framework's words, verbatim.</param>
/// <param name="Fix">The framework's fix suggestion, verbatim, when it gave one.</param>
/// <param name="Op">The op whose landed target best matches the pointer; best-effort — the pointer is authoritative.</param>
/// <param name="Code">A stable slug to branch on, where the stage has one.</param>
/// <param name="Severity"><c>error</c> blocks the change; <c>warning</c> does not.</param>
/// <param name="Skill">
/// The descriptor skill that states this violation's rule, when the turn has not loaded it (D50) — a catalogue name,
/// never author text. Not part of <see cref="Key"/>, so it never changes what the budget counts as the same refusal.
/// </param>
internal sealed record ToolViolation(
    string Source,
    string Pointer,
    string Message,
    string? Fix,
    int? Op = null,
    string? Code = null,
    string Severity = ToolViolation.ErrorSeverity,
    string? Skill = null)
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

    /// <summary>
    /// What "the same violation" means to the refusal budget (D41): the source, pointer, code and message, joined by
    /// U+001F — a separator none of them carries. The fix and the op are left out: they are hints, not the defect.
    /// </summary>
    internal string Key => string.Join('\u001f', Source, Pointer, Code ?? string.Empty, Message);

    /// <summary>The string <see cref="AssistantUpdate.Proposal.Refusals"/> has always carried.</summary>
    internal string AsRefusal() =>
        (Pointer.Length == 0 ? Message : $"{Pointer}: {Message}") + (Fix is null ? string.Empty : $" — {Fix}");
}
