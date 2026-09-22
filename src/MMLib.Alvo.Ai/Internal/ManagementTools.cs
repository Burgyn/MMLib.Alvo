using Microsoft.Extensions.AI;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

using System.Text.Json;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// The five things the agent may ask the framework — four reads and one dry run.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no apply, and the absence is the security property.</b> A prompt that says "never apply" is
/// an instruction a model can be argued out of; a tool set with no writing member is a capability it does
/// not have. <c>ManagementToolsTests</c> asserts both halves — the exact five names, and that the one tool
/// reaching the apply path always asks for a dry run.
/// </para>
/// <para>
/// <b>An instance per turn, because the proposal is state.</b> The agent proposes by validating, so the
/// last validated draft is what <see cref="AlvoAssistant"/> turns into
/// <see cref="AssistantUpdate.Proposal"/>. Built per turn, that state cannot outlive the conversation it
/// belongs to.
/// </para>
/// <para>
/// <b>Every tool answers, none of them throws.</b> A refusal the framework raised is something to tell the
/// operator, not a stack trace that ends the turn — so the documented management exceptions become results
/// the model can read, and everything else still reaches the host's logs as the bug it is.
/// </para>
/// </remarks>
internal sealed class ManagementTools
{
    private readonly IAlvoManagement _management;
    private readonly string _project;

    private ManagementTools(IAlvoManagement management, string project)
    {
        _management = management;
        _project = project;
        Functions =
        [
            AIFunctionFactory.Create(
                GetDescriptorAsync,
                "get_descriptor",
                "The project's descriptor as it is applied now, with the revision it is at."),
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
                ValidateDescriptorAsync,
                "validate_descriptor",
                "Runs a draft descriptor through the apply path as a dry run. Writes nothing. Returns the "
                + "migration plan it would run, or the refusals that stop it."),
        ];
    }

    /// <summary>The draft the agent last validated, or <see langword="null"/> when it validated none.</summary>
    internal ValidatedDraft? LastValidated { get; private set; }

    /// <summary>The tools, in the order they are declared.</summary>
    internal IReadOnlyList<AIFunction> Functions { get; }

    /// <summary>Builds the tool set for one turn over one project.</summary>
    /// <param name="management">The Management API, exactly as every other client reaches it.</param>
    /// <param name="project">The project every tool call is scoped to.</param>
    internal static ManagementTools For(IAlvoManagement management, string project)
    {
        ArgumentNullException.ThrowIfNull(management);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        return new ManagementTools(management, project);
    }

    /// <summary>The descriptor as it is applied now, and the revision it is at.</summary>
    private Task<string> GetDescriptorAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(await _management.GetDescriptorAsync(_project, ct).ConfigureAwait(false)));

    /// <summary>The resolved schema — what the descriptor became.</summary>
    private Task<string> GetSchemaAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(await _management.GetSchemaAsync(_project, ct).ConfigureAwait(false)));

    /// <summary>What this build honours and what it refuses, in the framework's own words.</summary>
    private Task<string> GetCapabilitiesAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(await _management.GetCapabilitiesAsync(_project, ct).ConfigureAwait(false)));

    /// <summary>The revision history, so the agent can say what changed and when.</summary>
    private Task<string> GetRevisionsAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(await _management.ListRevisionsAsync(_project, ct).ConfigureAwait(false)));

    /// <summary>
    /// Runs a draft through the apply path as a <b>dry run</b> and reports what it would do.
    /// </summary>
    /// <remarks>
    /// <c>DryRun: true</c> and <c>AllowDestructive: false</c> are literals rather than parameters. A tool
    /// whose caller could set either would be a tool the model could be talked into setting, and the whole
    /// design rests on the operator being the one who confirms a destructive change.
    /// </remarks>
    /// <param name="descriptorJson">The draft descriptor, whole.</param>
    /// <param name="expectedRevision">The revision the draft was written against.</param>
    /// <param name="ct">A token to cancel the call.</param>
    private Task<string> ValidateDescriptorAsync(string descriptorJson, int expectedRevision, CancellationToken ct) =>
        AnsweredAsync(async () =>
        {
            var request = new ManagementApplyRequest(
                descriptorJson, expectedRevision, AllowDestructive: false, DryRun: true);

            try
            {
                var result = await _management.ApplyDescriptorAsync(_project, request, ct).ConfigureAwait(false);

                return Validated(descriptorJson, expectedRevision, refusals: [], result.Plan);
            }
            catch (DescriptorValidationException refused)
            {
                return Validated(descriptorJson, expectedRevision, Refusals(refused), plan: null);
            }
            catch (DestructiveChangeNotAllowedException refused)
            {
                return Validated(descriptorJson, expectedRevision, [refused.Message], Destructive(refused.Plan));
            }
            catch (DescriptorConcurrencyException stale)
            {
                return Validated(descriptorJson, stale.ActualRevision, [stale.Message], plan: null);
            }
        });

    /// <summary>
    /// A refused destructive plan, as the summary this tool reports.
    /// </summary>
    /// <remarks>
    /// <b>The one path on which <c>hasDestructiveChanges</c> can be true.</b> The tool asks with
    /// <c>AllowDestructive: false</c>, so a destructive plan is a refusal rather than a result — and without
    /// this arm the field could only ever be <see langword="false"/>, which would leave the system prompt's
    /// "a dropped column is lost data" with no mechanism behind it. The step lines are the plan's own
    /// reasons, not a retelling.
    /// </remarks>
    /// <param name="plan">The plan the guardrail refused.</param>
    private static ManagementPlanSummary Destructive(MigrationPlan plan) => new(
        plan.IsEmpty,
        plan.HasDestructiveChanges,
        [.. plan.Steps.Where(step => step.Reason is { Length: > 0 }).Select(step => step.Reason!)]);

    /// <summary>The refusals, in the framework's own words and in document order.</summary>
    private static IReadOnlyList<string> Refusals(DescriptorValidationException refused) =>
    [
        .. refused.Result.Errors.Select(error =>
            $"{error.Path}: {error.Message}"
            + (error.FixSuggestion is null ? string.Empty : $" — {error.FixSuggestion}")),
    ];

    /// <summary>Files the validated draft, so a proposal can be built from it, and answers the model.</summary>
    private string Validated(
        string descriptorJson, int expectedRevision, IReadOnlyList<string> refusals, ManagementPlanSummary? plan)
    {
        LastValidated = new ValidatedDraft(descriptorJson, expectedRevision, refusals);

        return Json(new ValidationAnswer(
            refusals.Count == 0,
            plan?.IsEmpty ?? true,
            plan?.HasDestructiveChanges ?? false,
            plan?.Steps ?? [],
            refusals));
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
            return Error("invalid-request", refusal.Message);
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

    /// <summary>One draft the agent validated, and what the framework said about it.</summary>
    /// <param name="DescriptorJson">The draft, whole.</param>
    /// <param name="ExpectedRevision">The revision it was drafted against, which the apply must echo.</param>
    /// <param name="Refusals">What the dry run refused, verbatim.</param>
    internal sealed record ValidatedDraft(
        string DescriptorJson, int ExpectedRevision, IReadOnlyList<string> Refusals);
}

/// <summary>What <c>validate_descriptor</c> tells the model.</summary>
/// <param name="Valid">Whether the draft would apply.</param>
/// <param name="PlanIsEmpty">Whether it changes nothing about the schema — which a rules-only edit does.</param>
/// <param name="HasDestructiveChanges">Whether at least one step discards data.</param>
/// <param name="Steps">The plan's steps, in the framework's own summary wording.</param>
/// <param name="Refusals">What stops it, verbatim.</param>
internal sealed record ValidationAnswer(
    bool Valid,
    bool PlanIsEmpty,
    bool HasDestructiveChanges,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Refusals);

/// <summary>One refused tool call.</summary>
/// <param name="Error">A stable slug the model can branch on.</param>
/// <param name="Message">The framework's own refusal, unreworded.</param>
internal sealed record ToolError(string Error, string Message);
