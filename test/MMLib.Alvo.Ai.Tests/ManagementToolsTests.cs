using Microsoft.Extensions.AI;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The tool set, and what it exists to hold: it cannot write, it cannot crash a turn, and a patch keeps every byte
/// it did not touch.
/// </summary>
public sealed class ManagementToolsTests
{
    private const string Project = "p";
    private const int Revision = 7;
    private const string Descriptor = """{"name":"p","entities":{"bikes":{"fields":{"brand":{"type":"string"}}}}}""";
    private const string AddNotes = """[{"op":"add","path":"/entities/bikes/fields/notes","value":{"type":"text"}}]""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The model can call exactly these six tools, and nothing else.</summary>
    /// <remarks>
    /// Equality rather than "does not contain apply": a seventh tool somebody adds is a capability the model gains,
    /// and a test that only looked for forbidden names would pass for every one nobody thought to forbid.
    /// </remarks>
    [Fact]
    public void The_tool_set_is_exactly_the_four_reads_and_the_two_dry_runs() =>
        ManagementTools.For(Substitute.For<IAlvoManagement>(), Project).Functions
            .Select(tool => tool.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(["check_change", "get_capabilities", "get_descriptor", "get_revisions", "get_schema", "propose_change"]);

    [Theory]
    [InlineData("check_change")]
    [InlineData("propose_change")]
    public async Task A_write_shaped_tool_only_ever_dry_runs_and_never_allows_destruction(string tool)
    {
        var management = Accepting(Serving(Descriptor));

        await InvokeAsync(ManagementTools.For(management, Project), tool, Change(tool, AddNotes));

        await management.Received(1).ApplyDescriptorAsync(
            Project,
            Arg.Is<ManagementApplyRequest>(request => request.DryRun && !request.AllowDestructive),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_descriptor_hands_the_model_an_object_rather_than_a_string()
    {
        var answer = JsonNode.Parse(await InvokeAsync(Serving(Descriptor), "get_descriptor", []))!;

        answer["descriptor"].ShouldBeOfType<JsonObject>();
        answer["revision"]!.GetValue<int>().ShouldBe(Revision);
    }

    [Fact]
    public async Task A_tool_result_keeps_diacritics_quotes_and_plus_literal()
    {
        var answer = await InvokeAsync(Serving("""{"name":"p","description":"Dielňa \"U Ťava\" + ž"}"""), "get_descriptor", []);

        answer.ShouldContain("Dielňa");
        answer.ShouldContain("+ ž");
        answer.ShouldNotContain("\\u");
    }

    [Fact]
    public async Task A_patched_proposal_keeps_its_diacritics_literal_and_every_untouched_part_equal()
    {
        const string original = """{"name":"p","description":"č ť ž ô \"quoted\" +","entities":{"bikes":{"fields":{"brand":{"type":"string"}}}}}""";
        var management = Accepting(Serving(original));
        var tools = ManagementTools.For(management, Project);

        await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes));

        var proposed = tools.Proposal.ShouldNotBeNull().DescriptorJson;
        proposed.ShouldContain("č ť ž ô");
        proposed.ShouldNotContain("\\u");
        var untouched = JsonNode.Parse(proposed)!;
        untouched["entities"]!["bikes"]!["fields"]!.AsObject().Remove("notes");
        JsonNode.DeepEquals(untouched, JsonNode.Parse(original)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_validation_error_is_a_violation_at_its_pointer_naming_the_op_that_caused_it()
    {
        var management = Serving(Descriptor);
        Refusing(management, new DescriptorValidationException(new DescriptorValidationResult(
        [
            new DescriptorValidationError(
                "/entities/bikes/fields/gross/computed",
                "Field 'bikes.gross' declares both 'computed' and 'default'.",
                "Remove one.",
                DescriptorValidationSeverity.Error),
        ])));
        const string operations = """
            [{"op":"add","path":"/entities/bikes/fields/notes","value":{"type":"text"}},
             {"op":"add","path":"/entities/bikes/fields/gross","value":{"type":"decimal"}}]
            """;

        var violation = Violations(await InvokeAsync(management, "propose_change", Change("propose_change", operations))).Single();

        violation["source"]!.GetValue<string>().ShouldBe("validation");
        violation["pointer"]!.GetValue<string>().ShouldBe("/entities/bikes/fields/gross/computed");
        violation["message"]!.GetValue<string>().ShouldBe("Field 'bikes.gross' declares both 'computed' and 'default'.");
        violation["fix"]!.GetValue<string>().ShouldBe("Remove one.");
        violation["op"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_proposal_keeps_the_frameworks_own_wording_on_the_card()
    {
        var management = Serving(Descriptor);
        Refusing(management, new DescriptorValidationException(new DescriptorValidationResult(
        [
            new DescriptorValidationError(
                "/entities/invoices/fields/gross",
                "Field 'invoices.gross' declares both 'computed' and 'default'.",
                "Remove one.",
                DescriptorValidationSeverity.Error),
        ])));
        var tools = ManagementTools.For(management, Project);

        await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes));

        tools.Proposal!.Refusals.ShouldBe([
            "/entities/invoices/fields/gross: Field 'invoices.gross' declares both 'computed' and 'default'."
            + " — Remove one."
        ]);
    }

    [Fact]
    public async Task A_stale_base_is_a_concurrency_violation_carrying_the_current_revision_and_runs_nothing()
    {
        var management = Serving(Descriptor);

        var outcome = JsonNode.Parse(await InvokeAsync(
            management, "propose_change", Change("propose_change", AddNotes, baseRevision: Revision - 1)))!;

        outcome["revision"]!.GetValue<int>().ShouldBe(Revision);
        outcome["violations"]![0]!["source"]!.GetValue<string>().ShouldBe("concurrency");
        outcome["violations"]![0]!["code"]!.GetValue<string>().ShouldBe("stale-revision");
        await management.DidNotReceiveWithAnyArgs().ApplyDescriptorAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task An_access_change_by_a_developer_is_an_access_violation_rather_than_a_failed_turn()
    {
        var management = Serving(Descriptor);
        Refusing(management, new ManagementEscalationException());

        var violation = Violations(await InvokeAsync(management, "propose_change", Change("propose_change", AddNotes))).Single();

        violation["source"]!.GetValue<string>().ShouldBe("access");
        violation["pointer"]!.GetValue<string>().ShouldBe("/access");
    }

    [Fact]
    public async Task A_destructive_plan_is_a_plan_violation_with_the_plans_own_reasons()
    {
        var management = Serving(Descriptor);
        Refusing(management, new DestructiveChangeNotAllowedException(Project, new MigrationPlan
        {
            Steps = [new MigrationStep(
                new SchemaChange { Kind = SchemaChangeKind.DropField, Entity = "regions", Field = "code" },
                IsDestructive: true,
                "Dropping regions.code discards every value in it.")],
        }));
        var tools = ManagementTools.For(management, Project);

        var answer = await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes));

        answer.ShouldContain("\"hasDestructiveChanges\":true");
        answer.ShouldContain("Dropping regions.code discards every value in it.");
        Violations(answer).Single()["source"]!.GetValue<string>().ShouldBe("plan");
        Violations(answer).Single()["pointer"]!.GetValue<string>().ShouldBe("/entities/regions/fields/code");
        tools.Proposal!.Refusals.ShouldHaveSingleItem().ShouldContain("destructive");
    }

    [Fact]
    public async Task A_pointer_into_nothing_is_a_patch_violation_listing_what_exists()
    {
        const string operations = """[{"op":"add","path":"/entities/bike/fields/notes","value":{"type":"text"}}]""";

        var violation = Violations(await InvokeAsync(Serving(Descriptor), "propose_change", Change("propose_change", operations))).Single();

        violation["source"]!.GetValue<string>().ShouldBe("patch");
        violation["op"]!.GetValue<int>().ShouldBe(0);
        violation["fix"]!.GetValue<string>().ShouldContain("bikes");
    }

    [Fact]
    public async Task A_whole_document_replace_is_refused_before_the_dry_run()
    {
        var management = Serving(Descriptor);
        const string operations = """[{"op":"replace","path":"","value":{"name":"p"}}]""";

        var violation = Violations(await InvokeAsync(management, "propose_change", Change("propose_change", operations))).Single();

        violation["code"]!.GetValue<string>().ShouldBe("whole-document-replace");
        await management.DidNotReceiveWithAnyArgs().ApplyDescriptorAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Operations_sent_as_a_json_string_are_read_as_the_patch_they_spell()
    {
        var management = Accepting(Serving(Descriptor));
        var arguments = Change("propose_change", AddNotes);
        arguments["operations"] = JsonSerializer.SerializeToElement(AddNotes);

        var outcome = JsonNode.Parse(await InvokeAsync(management, "propose_change", arguments))!;

        outcome["valid"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task The_last_valid_proposal_survives_a_later_refused_attempt()
    {
        var management = Serving(Descriptor);
        management.ApplyDescriptorAsync(Project, Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new ManagementApplyResult(Applied: false, Revision, EmptyPlan),
                _ => throw new DescriptorValidationException(new DescriptorValidationResult(
                    [new DescriptorValidationError("/entities", "No.", null, DescriptorValidationSeverity.Error)])));
        var tools = ManagementTools.For(management, Project);

        await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes));
        await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes));

        tools.Proposal!.Refusals.ShouldBeEmpty();
        JsonNode.Parse(tools.Proposal.DescriptorJson)!["entities"]!["bikes"]!["fields"]!["notes"].ShouldNotBeNull();
    }

    [Fact]
    public async Task The_fourth_attempt_after_three_refusals_gets_only_the_budget_violation()
    {
        var management = Serving(Descriptor);
        Refusing(management, new DescriptorValidationException(new DescriptorValidationResult(
            [new DescriptorValidationError("/entities", "No.", null, DescriptorValidationSeverity.Error)])));
        var tools = ManagementTools.For(management, Project);

        var left = new List<int>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var outcome = JsonNode.Parse(await InvokeAsync(tools, "check_change", Change("check_change", AddNotes)))!;
            left.Add(outcome["attemptsLeft"]!.GetValue<int>());
        }

        var fourth = Violations(await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes))).Single();

        left.ShouldBe([2, 1, 0]);
        fourth["source"]!.GetValue<string>().ShouldBe("budget");
        fourth["message"]!.GetValue<string>()
            .ShouldBe("Stop proposing. Explain to the operator what the framework refused, quoting it.");
        await management.ReceivedWithAnyArgs(3).ApplyDescriptorAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_spent_budget_reports_the_revision_the_descriptor_is_at_rather_than_the_claimed_base()
    {
        var tools = ManagementTools.For(Serving(Descriptor), Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumRefusedAttempts; attempt++)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", AddNotes, baseRevision: 1));
        }

        var outcome = JsonNode.Parse(await InvokeAsync(tools, "check_change", Change("check_change", AddNotes, baseRevision: 1)))!;

        outcome["violations"]![0]!["source"]!.GetValue<string>().ShouldBe("budget");
        outcome["revision"]!.GetValue<int>().ShouldBe(Revision);
    }

    [Fact]
    public async Task Check_change_files_no_proposal()
    {
        var tools = ManagementTools.For(Accepting(Serving(Descriptor)), Project);

        await InvokeAsync(tools, "check_change", Change("check_change", AddNotes));

        tools.Proposal.ShouldBeNull();
    }

    [Fact]
    public async Task A_read_only_turn_leaves_no_proposal_behind()
    {
        var tools = ManagementTools.For(Serving(Descriptor), Project);

        await InvokeAsync(tools, "get_descriptor", []);

        tools.Proposal.ShouldBeNull();
    }

    [Fact]
    public async Task A_forbidden_caller_is_reported_to_the_model_rather_than_crashing_the_turn()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetSchemaAsync(Project, Arg.Any<CancellationToken>()).Throws(new ManagementForbiddenException());

        (await InvokeAsync(management, "get_schema", [])).ShouldContain("forbidden");
    }

    [Fact]
    public async Task A_read_that_races_an_apply_is_reported_to_the_model()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetDescriptorAsync(Project, Arg.Any<CancellationToken>())
            .Throws(new DescriptorConcurrencyException(Project, expectedRevision: 1, actualRevision: 2));

        (await InvokeAsync(management, "get_descriptor", [])).ShouldContain("stale-revision");
    }

    private static IAlvoManagement Serving(string descriptorJson)
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetDescriptorAsync(Project, Arg.Any<CancellationToken>())
            .Returns(new ManagementDescriptor(Project, Revision, descriptorJson));

        return management;
    }

    private static IAlvoManagement Accepting(IAlvoManagement management)
    {
        management.ApplyDescriptorAsync(Project, Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementApplyResult(Applied: false, Revision, EmptyPlan));

        return management;
    }

    private static void Refusing(IAlvoManagement management, Exception refusal) =>
        management.ApplyDescriptorAsync(Project, Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Throws(refusal);

    private static Dictionary<string, object?> Change(string tool, string operations, int baseRevision = Revision)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["baseRevision"] = baseRevision,
            ["operations"] = JsonDocument.Parse(operations).RootElement.Clone(),
        };
        if (tool == "propose_change")
        {
            arguments["summary"] = "Adds notes to bikes.";
        }

        return arguments;
    }

    private static IReadOnlyList<JsonNode> Violations(string answer) =>
        [.. JsonNode.Parse(answer)!["violations"]!.AsArray().Select(violation => violation!)];

    private static Task<string> InvokeAsync(IAlvoManagement management, string tool, Dictionary<string, object?> arguments) =>
        InvokeAsync(ManagementTools.For(management, Project), tool, arguments);

    private static async Task<string> InvokeAsync(ManagementTools tools, string tool, Dictionary<string, object?> arguments)
    {
        var function = tools.Functions.Single(candidate => candidate.Name == tool);
        var result = await function.InvokeAsync(new AIFunctionArguments(arguments), Ct);

        return result?.ToString() ?? string.Empty;
    }

    private static ManagementPlanSummary EmptyPlan { get; } = new(IsEmpty: true, HasDestructiveChanges: false, []);
}
