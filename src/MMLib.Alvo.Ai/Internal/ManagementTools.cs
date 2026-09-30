using Microsoft.Extensions.AI;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// The six things the agent may ask the framework — four reads and two dry runs over an RFC 6902 patch.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no apply, and the absence is the security property.</b> A prompt that says "never apply" is
/// an instruction a model can be argued out of; a tool set with no writing member is a capability it does
/// not have. <c>ManagementToolsTests</c> asserts both halves — the exact six names, and that both
/// write-shaped tools only ever ask for a dry run.
/// </para>
/// <para>
/// <b>An instance per turn, because the proposal and the budget are state.</b> The last <em>valid</em>
/// <c>propose_change</c> is the proposal; with none valid, the last refused one is, carrying its refusals.
/// Built per turn, none of that can outlive the conversation it belongs to.
/// </para>
/// <para>
/// <b>The budget measures progress (D41).</b> A refusal spends one of three attempts only when it made no progress:
/// the same blocking violations as the refusal before it, or more (the first refusal is measured against none, so it
/// always spends one). Six refusals are the ceiling, progress or not — and the ceiling is what bounds a model that
/// oscillates between two defect sets, or keeps retrying a CEL expression that is refused differently each time.
/// A patch not yet refused is always dry-run before a budget answer: only one that was already refused, against the
/// same base revision, is answered without a dry run once the three are spent, and that answer says it was not
/// checked (<c>unchecked: true</c>). It quotes that patch's own earlier refusal (the last refusal, when the ceiling
/// stopped a patch never refused), and says so when a valid proposal is already filed. A patch that was checked <em>valid</em> is never
/// remembered, so "would this work?" followed by the same <c>propose_change</c> is dry-run twice and filed.
/// </para>
/// <para>
/// <b>Only a refusal spends budget.</b> An attempt that ends in an exception — one <c>AnsweredAsync</c> maps, a
/// call the invoker could not bind, or a bug — spends nothing, and neither does a <em>valid</em> dry run; what
/// bounds those is <see cref="AlvoAssistant.MaximumIterations"/>, which caps model round-trips (not calls per
/// round-trip — the eval's tool-call ceiling is the guard there).
/// </para>
/// <para>
/// <b>The state assumes sequential invocation.</b> The budget check, the two counts, the refused patches, the last
/// blocking set and the filed proposal are plain fields, correct because the agent's function invoker runs one call
/// at a time (<c>AllowConcurrentInvocation</c> off, set so explicitly and pinned by <c>AlvoAssistantTests</c>). Turning that
/// on would let parallel calls all pass an exhausted budget.
/// </para>
/// <para>
/// <b>Every tool answers, none of them throws.</b> A refusal the framework raised is something to tell the
/// operator, not a stack trace that ends the turn — so the documented management exceptions become results
/// the model can read, and everything else still reaches the host's logs as the bug it is.
/// </para>
/// </remarks>
internal sealed class ManagementTools
{
    /// <summary>How many refusals that made no progress one turn may make (D41).</summary>
    internal const int MaximumStalledRefusals = 3;

    /// <summary>How many refusals one turn may make in all, progress or not — the hard ceiling (D41).</summary>
    internal const int MaximumRefusals = 6;

    private const string BaseRevisionHelp =
        "The revision get_descriptor returned. A change written against another revision is refused; read again.";

    private const string OperationsHelp =
        "An RFC 6902 JSON Patch: an array of {op, path, value?, from?}, op one of add, remove, replace, move, copy, "
        + "test, path an RFC 6901 JSON Pointer such as /entities/customers/fields/full_name. Never the whole document (\"\").";

    private const string SummaryHelp = "One sentence, in the operator's language, saying what the change does.";

    private const string InvalidRequestCode = "invalid-request";

    private const string SummaryRequired =
        "propose_change needs a summary: one sentence, in the operator's language, saying what the change does. "
        + "Nothing was dry-run or filed.";

    private readonly IAlvoManagement _management;
    private readonly string _project;
    private readonly List<RefusedPatch> _refused = [];
    private HashSet<string> _lastBlocking = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _lastRefusals = [];
    private int _stalledRefusals;
    private int _refusals;
    private int _currentRevision;
    private ProposedDraft? _lastValid;
    private ProposedDraft? _lastRefused;

    private ManagementTools(IAlvoManagement management, string project)
    {
        _management = management;
        _project = project;
        Functions =
        [
            AIFunctionFactory.Create(
                GetDescriptorAsync,
                "get_descriptor",
                "The project's descriptor as it is applied now, as a JSON object, with the revision it is at."),
            AIFunctionFactory.Create(
                GetSchemaAsync,
                "get_schema",
                "The resolved schema — entities, fields and facets as the descriptor became them."),
            AIFunctionFactory.Create(
                GetCapabilitiesAsync,
                "get_capabilities",
                "What this build honours and what it refuses, in the framework's own words."),
            AIFunctionFactory.Create(
                GetRevisionsAsync,
                "get_revisions",
                "The revision history: who applied what, when, and why."),
            AIFunctionFactory.Create(
                CheckChangeAsync,
                "check_change",
                "Dry-runs a JSON Patch against the descriptor. Files nothing. Only for 'would this work?' questions."),
            AIFunctionFactory.Create(
                ProposeChangeAsync,
                "propose_change",
                "Dry-runs a JSON Patch against the descriptor; when valid, it becomes the proposal the operator "
                + "reviews and applies. Writes nothing."),
        ];
    }

    /// <summary>What this turn proposes: the last valid <c>propose_change</c>, else the last refused one, else none.</summary>
    internal ProposedDraft? Proposal => _lastValid ?? _lastRefused;

    /// <summary>The tools, in the order they are declared.</summary>
    internal IReadOnlyList<AIFunction> Functions { get; }

    /// <summary>The smaller of the two remainders: stalled refusals left, and refusals left in all.</summary>
    private int AttemptsLeft =>
        Math.Max(0, Math.Min(MaximumStalledRefusals - _stalledRefusals, MaximumRefusals - _refusals));

    /// <summary>Builds the tool set for one turn over one project.</summary>
    /// <param name="management">The Management API, exactly as every other client reaches it.</param>
    /// <param name="project">The project every tool call is scoped to.</param>
    internal static ManagementTools For(IAlvoManagement management, string project)
    {
        ArgumentNullException.ThrowIfNull(management);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        return new ManagementTools(management, project);
    }

    /// <summary>The descriptor as it is applied now — as an object, never a string of JSON — and its revision.</summary>
    private Task<string> GetDescriptorAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(DescriptorView.Of(await _management.GetDescriptorAsync(_project, ct).ConfigureAwait(false))));

    /// <summary>The resolved schema — what the descriptor became.</summary>
    private Task<string> GetSchemaAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(await _management.GetSchemaAsync(_project, ct).ConfigureAwait(false)));

    /// <summary>What this build honours and what it refuses, in the framework's own words.</summary>
    private Task<string> GetCapabilitiesAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(await _management.GetCapabilitiesAsync(_project, ct).ConfigureAwait(false)));

    /// <summary>The revision history, so the agent can say what changed and when.</summary>
    private Task<string> GetRevisionsAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(await _management.ListRevisionsAsync(_project, ct).ConfigureAwait(false)));

    /// <summary>Dry-runs a patch and files nothing — the answer to "would this work?".</summary>
    /// <param name="baseRevision">The revision the operations were written against.</param>
    /// <param name="operations">The RFC 6902 patch.</param>
    /// <param name="ct">A token to cancel the call.</param>
    private Task<string> CheckChangeAsync(
        [Description(BaseRevisionHelp)] int baseRevision,
        [Description(OperationsHelp)] JsonElement operations,
        CancellationToken ct) =>
        AnsweredAsync(async () => Json((await AttemptAsync(baseRevision, operations, ct).ConfigureAwait(false)).Outcome));

    /// <summary>Dry-runs a patch and files the result as this turn's proposal.</summary>
    /// <param name="baseRevision">The revision the operations were written against.</param>
    /// <param name="operations">The RFC 6902 patch.</param>
    /// <param name="summary">What the change does, in one sentence — the card's fallback summary.</param>
    /// <param name="ct">A token to cancel the call.</param>
    private Task<string> ProposeChangeAsync(
        [Description(BaseRevisionHelp)] int baseRevision,
        [Description(OperationsHelp)] JsonElement operations,
        [Description(SummaryHelp)] string summary,
        CancellationToken ct) =>
        AnsweredAsync(() => ProposeAsync(baseRevision, operations, summary, ct));

    /// <summary>Refuses a proposal with no summary as a request; otherwise attempts it and files what it produced.</summary>
    /// <remarks>
    /// A missing summary spends no budget and runs no dry run: it is a malformed call, not a change the framework
    /// refused — and it is never quietly run as a <c>check_change</c>, which would answer "valid" and file nothing.
    /// </remarks>
    private async Task<string> ProposeAsync(int baseRevision, JsonElement operations, string? summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return Error(InvalidRequestCode, SummaryRequired);
        }

        var (attempt, outcome) = await AttemptAsync(baseRevision, operations, ct).ConfigureAwait(false);
        if (attempt is not null)
        {
            FileProposal(attempt, summary);
        }

        return Json(outcome);
    }

    /// <summary>One attempt at a change, within the budget: the draft it produced, if any, and the model's answer.</summary>
    private async Task<(DraftAttempt? Attempt, ChangeOutcome Outcome)> AttemptAsync(
        int baseRevision, JsonElement operations, CancellationToken ct)
    {
        var earlier = RefusedBefore(baseRevision, DescriptorDraft.Unwrapped(operations));
        if (_refusals >= MaximumRefusals || (_stalledRefusals >= MaximumStalledRefusals && earlier is not null))
        {
            return (null, BudgetSpent(earlier));
        }

        var attempt = await DescriptorDraft.BuildAsync(_management, _project, baseRevision, operations, ct).ConfigureAwait(false);
        Record(attempt, baseRevision, operations);

        return (attempt, ChangeOutcome.From(attempt, AttemptsLeft));
    }

    /// <summary>This exact patch's earlier refusal, when it was refused before in this turn; else none.</summary>
    /// <param name="baseRevision">The revision the patch was written against.</param>
    /// <param name="patch">The patch, unwrapped — so an array and its string form are the same patch.</param>
    private RefusedPatch? RefusedBefore(int baseRevision, JsonElement patch) =>
        _refused.Find(done => done.BaseRevision == baseRevision && JsonElement.DeepEquals(done.Operations, patch));

    /// <summary>
    /// The unchecked answer: it quotes the patch's own earlier refusal when there is one, else the last refusal, and
    /// says when a valid proposal is already filed, so the model does not report a failure the card contradicts.
    /// </summary>
    private ChangeOutcome BudgetSpent(RefusedPatch? earlier) => ChangeOutcome.BudgetSpent(
        _currentRevision,
        ViolationMapping.BudgetSpent(earlier?.Refusals ?? _lastRefusals, ownRefusal: earlier is not null, proposalFiled: _lastValid is not null));

    /// <summary>Remembers the revision the attempt learned the descriptor is at, and counts a refusal.</summary>
    private void Record(DraftAttempt attempt, int baseRevision, JsonElement operations)
    {
        _currentRevision = attempt.CurrentRevision;
        if (!attempt.Valid)
        {
            Refused(attempt, baseRevision, operations);
        }
    }

    /// <summary>Counts a refusal, remembers the patch, and spends an attempt only when it made no progress (D41).</summary>
    private void Refused(DraftAttempt attempt, int baseRevision, JsonElement operations)
    {
        var blocking = attempt.Violations
            .Where(violation => violation.Blocks)
            .Select(violation => violation.Key)
            .ToHashSet(StringComparer.Ordinal);
        _refused.Add(new RefusedPatch(baseRevision, DescriptorDraft.Unwrapped(operations).Clone(), attempt.Refusals));
        _refusals++;
        _stalledRefusals += blocking.IsSupersetOf(_lastBlocking) ? 1 : 0;
        (_lastBlocking, _lastRefusals) = (blocking, attempt.Refusals);
    }

    /// <summary>Files the attempt: a valid one as the proposal, a refused one as the fallback while none is valid.</summary>
    private void FileProposal(DraftAttempt attempt, string summary)
    {
        var draft = new ProposedDraft(attempt.DescriptorJson, attempt.Revision, summary, attempt.Refusals);
        if (attempt.Valid)
        {
            _lastValid = draft;
        }
        else
        {
            _lastRefused = draft;
        }
    }

    /// <summary>
    /// Runs one tool, turning a refusal the framework raised into an answer the model can act on.
    /// </summary>
    /// <remarks>
    /// Only the exceptions the Management API documents are caught. A bug in Alvo must still reach the host's
    /// logs as a bug — swallowing everything here is how a broken tool becomes a model that quietly says it
    /// cannot help.
    /// </remarks>
    private static async Task<string> AnsweredAsync(Func<Task<string>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ManagementForbiddenException refusal)
        {
            return Error("forbidden", refusal.Message);
        }
        catch (ManagementProjectNotFoundException refusal)
        {
            return Error("project-not-found", refusal.Message);
        }
        catch (ManagementRequestException refusal)
        {
            return Error(InvalidRequestCode, refusal.Message);
        }
        catch (DescriptorConcurrencyException stale)
        {
            return Error("stale-revision", stale.Message);
        }
    }

    /// <summary>One refused call, in the shape every tool reports a refusal in.</summary>
    private static string Error(string code, string message) => Json(new ToolError(code, message));

    /// <summary>The value as JSON, which is the only shape a tool result reaches a model in.</summary>
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ToolJson.Options);
}

/// <summary>What this turn proposes, as <see cref="AlvoAssistant"/> turns it into <see cref="AssistantUpdate.Proposal"/>.</summary>
/// <param name="DescriptorJson">The patched descriptor, whole — or the applied one, when the attempt produced no draft.</param>
/// <param name="ExpectedRevision">The revision it was drafted against, which the apply must echo.</param>
/// <param name="Summary">The model's one-sentence summary, used when the turn's answer is empty.</param>
/// <param name="Refusals">What the dry run refused, verbatim.</param>
internal sealed record ProposedDraft(string DescriptorJson, int ExpectedRevision, string Summary, IReadOnlyList<string> Refusals);

/// <summary>A patch this turn already had refused, with what it was refused with (D41).</summary>
/// <param name="BaseRevision">The revision it was written against.</param>
/// <param name="Operations">The patch, unwrapped.</param>
/// <param name="Refusals">Its blocking refusals, verbatim.</param>
internal sealed record RefusedPatch(int BaseRevision, JsonElement Operations, IReadOnlyList<string> Refusals);

/// <summary>What <c>get_descriptor</c> returns: the descriptor as an object, never a string of JSON.</summary>
/// <param name="Project">The project it belongs to.</param>
/// <param name="Revision">The revision it is at — the base every change is written against.</param>
/// <param name="Descriptor">The descriptor, parsed.</param>
internal sealed record DescriptorView(string Project, int Revision, JsonNode? Descriptor)
{
    internal static DescriptorView Of(ManagementDescriptor descriptor) =>
        new(descriptor.Project, descriptor.Revision, JsonNode.Parse(descriptor.DescriptorJson));
}

/// <summary>One refused tool call.</summary>
/// <param name="Error">A stable slug the model can branch on.</param>
/// <param name="Message">The framework's own refusal, unreworded.</param>
internal sealed record ToolError(string Error, string Message);
