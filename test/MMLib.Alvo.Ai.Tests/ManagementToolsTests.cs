using Microsoft.Extensions.AI;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using System.Text.Json;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The tool set, and the two properties it exists to hold: it cannot write, and it cannot crash a turn.
/// </summary>
public sealed class ManagementToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Exactly five tools, and none of them applies.
    /// </summary>
    /// <remarks>
    /// Asserted by equality rather than by "does not contain apply": a sixth tool somebody adds is a
    /// capability the model gains, and a test that only looked for forbidden names would pass for every one
    /// nobody thought to forbid.
    /// </remarks>
    [Fact]
    public void The_tool_set_is_exactly_the_four_reads_and_the_dry_run() =>
        ManagementTools.For(Substitute.For<IAlvoManagement>(), "p").Functions
            .Select(tool => tool.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(["get_capabilities", "get_descriptor", "get_revisions", "get_schema", "validate_descriptor"]);

    /// <summary>
    /// The one tool that reaches the apply path always asks for a dry run, and never for destruction.
    /// </summary>
    /// <remarks>
    /// This is the security property the whole design rests on, so it is measured on the request the tool
    /// actually built rather than read off the source.
    /// </remarks>
    [Fact]
    public async Task Validate_descriptor_never_applies()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementApplyResult(Applied: false, Revision: 3, EmptyPlan));

        await InvokeAsync(management, "validate_descriptor", Draft());

        await management.Received(1).ApplyDescriptorAsync(
            "p",
            Arg.Is<ManagementApplyRequest>(request => request.DryRun && !request.AllowDestructive),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A refused caller is something to tell the operator, not a stack trace that ends the turn.</summary>
    [Fact]
    public async Task A_forbidden_caller_is_reported_to_the_model_rather_than_crashing_the_turn()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetSchemaAsync("p", Arg.Any<CancellationToken>())
            .Throws(new ManagementForbiddenException());

        var answer = await InvokeAsync(management, "get_schema", []);

        answer.ShouldContain("forbidden");
    }

    /// <summary>
    /// A refused draft carries the framework's own words, and the draft is still remembered.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The wording is what the operator reads, and rewording it would make the sentence
    /// they see one nobody tested; remembering the draft is what lets the screen show what was refused
    /// rather than an empty panel.
    /// </remarks>
    [Fact]
    public async Task A_refused_draft_keeps_the_frameworks_own_wording()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Throws(new DescriptorValidationException(new DescriptorValidationResult(
            [
                new DescriptorValidationError(
                    "/entities/invoices/fields/gross",
                    "Field 'invoices.gross' declares both 'computed' and 'default'.",
                    "Remove one.",
                    DescriptorValidationSeverity.Error),
            ])));

        var tools = ManagementTools.For(management, "p");
        await InvokeAsync(tools, "validate_descriptor", Draft());

        tools.LastValidated!.Refusals.ShouldBe([
            "/entities/invoices/fields/gross: Field 'invoices.gross' declares both 'computed' and 'default'."
            + " — Remove one."
        ]);
    }

    /// <summary>A draft that passes is remembered with the revision it was written against.</summary>
    [Fact]
    public async Task A_validated_draft_remembers_the_revision_the_apply_must_echo()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementApplyResult(Applied: false, Revision: 7, EmptyPlan));

        var tools = ManagementTools.For(management, "p");
        await InvokeAsync(tools, "validate_descriptor", Draft(revision: 7));

        tools.LastValidated!.ExpectedRevision.ShouldBe(7);
        tools.LastValidated.Refusals.ShouldBeEmpty();
    }

    /// <summary>A turn that only read something proposes nothing.</summary>
    [Fact]
    public async Task A_read_only_turn_leaves_no_draft_behind()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetDescriptorAsync("p", Arg.Any<CancellationToken>())
            .Returns(new ManagementDescriptor("p", 2, "{}"));

        var tools = ManagementTools.For(management, "p");
        await InvokeAsync(tools, "get_descriptor", []);

        tools.LastValidated.ShouldBeNull();
    }

    /// <summary>
    /// A destructive plan is an answer the model can act on, not an exception that ends the turn.
    /// </summary>
    /// <remarks>
    /// The tool asks with <c>AllowDestructive: false</c>, so the guardrail refuses rather than returns —
    /// which makes this the only path on which <c>hasDestructiveChanges</c> can be true. Without the arm
    /// the field was unreachable and the operator was shown "the AI endpoint did not answer" for a refusal
    /// the framework had spelled out.
    /// </remarks>
    [Fact]
    public async Task A_destructive_plan_is_reported_rather_than_thrown()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Throws(new DestructiveChangeNotAllowedException("p", new MigrationPlan
            {
                Steps = [new MigrationStep(
                    new SchemaChange { Kind = SchemaChangeKind.DropField, Entity = "regions", Field = "code" },
                    IsDestructive: true,
                    "Dropping regions.code discards every value in it.")],
            }));

        var tools = ManagementTools.For(management, "p");
        var answer = await InvokeAsync(tools, "validate_descriptor", Draft());

        answer.ShouldContain("\"hasDestructiveChanges\":true");
        answer.ShouldContain("Dropping regions.code discards every value in it.");
        tools.LastValidated!.Refusals.ShouldHaveSingleItem().ShouldContain("destructive");
    }

    /// <summary>
    /// A draft written against a revision that has since moved is an answer too.
    /// </summary>
    /// <remarks>
    /// Routine rather than exotic: the agent reads a revision, drafts, then validates, and another
    /// operator's apply in between is all it takes.
    /// </remarks>
    [Fact]
    public async Task A_stale_revision_is_reported_with_the_one_that_is_current()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Throws(new DescriptorConcurrencyException("p", expectedRevision: 1, actualRevision: 4));

        var tools = ManagementTools.For(management, "p");
        var answer = await InvokeAsync(tools, "validate_descriptor", Draft());

        answer.ShouldContain("\"valid\":false");
        tools.LastValidated!.ExpectedRevision.ShouldBe(4);
    }

    /// <summary>And a read that raced the same apply reports it rather than ending the turn.</summary>
    [Fact]
    public async Task A_read_that_races_an_apply_is_reported_to_the_model()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetDescriptorAsync("p", Arg.Any<CancellationToken>())
            .Throws(new DescriptorConcurrencyException("p", expectedRevision: 1, actualRevision: 2));

        (await InvokeAsync(management, "get_descriptor", [])).ShouldContain("stale-revision");
    }

    private static Task<string> InvokeAsync(
        IAlvoManagement management, string tool, Dictionary<string, object?> arguments) =>
        InvokeAsync(ManagementTools.For(management, "p"), tool, arguments);

    private static async Task<string> InvokeAsync(
        ManagementTools tools, string tool, Dictionary<string, object?> arguments)
    {
        var function = tools.Functions.Single(candidate => candidate.Name == tool);
        var result = await function.InvokeAsync(new AIFunctionArguments(arguments), Ct);

        return result?.ToString() ?? string.Empty;
    }

    private static Dictionary<string, object?> Draft(int revision = 1) => new()
    {
        ["descriptorJson"] = """{"name":"p"}""",
        ["expectedRevision"] = revision,
    };

    private static ManagementPlanSummary EmptyPlan { get; } = new(IsEmpty: true, HasDestructiveChanges: false, []);
}
