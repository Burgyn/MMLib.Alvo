using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// The one pipeline both write-shaped tools share: read, check the base, admit, patch, serialize, dry-run, map.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model never re-emits text it did not change.</b> The descriptor it patches is the applied one, read here;
/// only its operations cross the tool boundary — one encoding level, no retyped Slovak, no retyped CEL.
/// </para>
/// <para>
/// <b>Serialized as the dashboard's working copy writes</b> — indented, <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
/// — so Preview's diff shows only the change and <c>č</c> stays <c>č</c>. The relaxed encoder is safe here because
/// the text reaches the apply path and a Razor-encoded diff, never raw HTML.
/// </para>
/// <para>
/// <c>DryRun: true</c> and <c>AllowDestructive: false</c> are literals rather than parameters: a tool whose caller
/// could set either would be a tool the model could be talked into setting.
/// </para>
/// </remarks>
internal static class DescriptorDraft
{
    private const string WholeDocumentRemoved = "An admitted patch cannot remove the whole document.";

    private static readonly JsonSerializerOptions _descriptorWriter = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Patches the applied descriptor at <paramref name="baseRevision"/> and dry-runs the result.</summary>
    internal static async Task<DraftAttempt> BuildAsync(
        IAlvoManagement management, string project, int baseRevision, JsonElement operations, CancellationToken ct)
    {
        var current = await management.GetDescriptorAsync(project, ct).ConfigureAwait(false);
        if (current.Revision != baseRevision)
        {
            return DraftAttempt.Refused(current, ViolationMapping.Stale(current.Revision, baseRevision));
        }

        var patch = Unwrapped(operations);
        if (PatchAdmission.Check(patch) is { } inadmissible)
        {
            return DraftAttempt.Refused(current, ViolationMapping.FromPatch(inadmissible));
        }

        var patched = JsonPatch.Apply(JsonNode.Parse(current.DescriptorJson), patch);
        if (patched.Error is { } failed)
        {
            return DraftAttempt.Refused(current, ViolationMapping.FromPatch(failed));
        }

        var document = patched.Document ?? throw new InvalidOperationException(WholeDocumentRemoved);
        var draft = new Draft(document.ToJsonString(_descriptorWriter), current.Revision, patched.ChangedPaths, patched.Targets);
        return await DryRunAsync(management, project, draft, ct).ConfigureAwait(false);
    }

    private static async Task<DraftAttempt> DryRunAsync(
        IAlvoManagement management, string project, Draft draft, CancellationToken ct)
    {
        var request = new ManagementApplyRequest(draft.DescriptorJson, draft.Revision, AllowDestructive: false, DryRun: true);
        try
        {
            var result = await management.ApplyDescriptorAsync(project, request, ct).ConfigureAwait(false);
            return draft.Accepted(result.Plan);
        }
        catch (DescriptorValidationException refused)
        {
            return draft.Refused(ViolationMapping.FromValidation(refused, draft.Targets));
        }
        catch (DestructiveChangeNotAllowedException refused)
        {
            return draft.Refused([ViolationMapping.FromDestructive(refused, draft.Targets)], Destructive(refused.Plan));
        }
        catch (DescriptorConcurrencyException stale)
        {
            return draft.Refused([ViolationMapping.FromConcurrency(stale)]) with { ActualRevision = stale.ActualRevision };
        }
        catch (ManagementEscalationException escalation)
        {
            return draft.Refused([ViolationMapping.FromEscalation(escalation)]);
        }
    }

    /// <summary>A refused destructive plan, as the summary the outcome reports.</summary>
    /// <remarks>
    /// <b>The one path on which <c>hasDestructiveChanges</c> can be true.</b> The dry run asks with
    /// <c>AllowDestructive: false</c>, so a destructive plan is a refusal rather than a result — and without
    /// this arm the field could only ever be <see langword="false"/>, which would leave the instructions'
    /// "a dropped column is lost data" with no mechanism behind it. The step lines are the plan's own
    /// reasons, not a retelling.
    /// </remarks>
    /// <param name="plan">The plan the guardrail refused.</param>
    private static ManagementPlanSummary Destructive(MigrationPlan plan) => new(
        plan.IsEmpty,
        plan.HasDestructiveChanges,
        [.. plan.Steps.Where(step => step.Reason is { Length: > 0 }).Select(step => step.Reason!)]);

    /// <summary>The patch itself: a string of JSON is parsed, so an array and its string form are one patch.</summary>
    internal static JsonElement Unwrapped(JsonElement operations) =>
        operations.ValueKind == JsonValueKind.String && TryParse(operations.GetString()!, out var parsed) ? parsed : operations;

    private static bool TryParse(string text, out JsonElement parsed)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            parsed = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            parsed = default;
            return false;
        }
    }

    private sealed record Draft(string DescriptorJson, int Revision, IReadOnlyList<string> ChangedPaths, IReadOnlyList<string?> Targets)
    {
        internal DraftAttempt Accepted(ManagementPlanSummary plan) =>
            new(DescriptorJson, Revision, Valid: true, plan, ChangedPaths, []);

        internal DraftAttempt Refused(IReadOnlyList<ToolViolation> violations, ManagementPlanSummary? plan = null) =>
            new(DescriptorJson, Revision, Valid: false, plan, ChangedPaths, violations);
    }
}

/// <summary>One attempt at a change: the draft it produced, and what the framework said about it.</summary>
internal sealed record DraftAttempt(
    string DescriptorJson,
    int Revision,
    bool Valid,
    ManagementPlanSummary? Plan,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<ToolViolation> Violations)
{
    /// <summary>
    /// The revision the descriptor turned out to be at, when a concurrent apply moved it during the dry run.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Revision"/>, which stays the revision the draft was written against — the one a
    /// proposal filed from it must carry, because its descriptor is the patch applied to that revision.
    /// </remarks>
    internal int? ActualRevision { get; init; }

    /// <summary>The revision the descriptor is at, as far as this attempt learned — what the model re-bases on.</summary>
    internal int CurrentRevision => ActualRevision ?? Revision;

    /// <summary>The blocking violations, in the string form the proposal card has always drawn.</summary>
    internal IReadOnlyList<string> Refusals => [.. Violations.Where(violation => violation.Blocks).Select(violation => violation.AsRefusal())];

    /// <summary>An attempt that produced no draft (D4): it carries the applied descriptor, unchanged.</summary>
    internal static DraftAttempt Refused(ManagementDescriptor current, params IReadOnlyList<ToolViolation> violations) =>
        new(current.DescriptorJson, current.Revision, Valid: false, Plan: null, [], violations);
}
