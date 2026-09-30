using Microsoft.Extensions.AI;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using System.Globalization;
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
    private const string WithHooks =
        """{"name":"p","entities":{"bikes":{"fields":{"brand":{"type":"string"}},"hooks":{"beforeCreate":[{"url":"a"},{"url":"b"}]}}}}""";
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

    /// <summary>
    /// Both tools that reach the apply path always ask for a dry run, and never for destruction.
    /// </summary>
    /// <remarks>
    /// This is the security property the whole design rests on, so it is measured on the request the tool
    /// actually built rather than read off the source.
    /// </remarks>
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

    /// <summary>
    /// The descriptor reaches the model as an object, so a patch is written against structure rather than text.
    /// </summary>
    /// <remarks>
    /// A string of JSON inside JSON is the second encoding level the model then has to undo by hand — and the
    /// escape sequences it hallucinated there are how the live failure began.
    /// </remarks>
    [Fact]
    public async Task Get_descriptor_hands_the_model_an_object_rather_than_a_string()
    {
        var answer = JsonNode.Parse(await InvokeAsync(Serving(Descriptor), "get_descriptor", []))!;

        answer["descriptor"].ShouldBeOfType<JsonObject>();
        answer["revision"]!.GetValue<int>().ShouldBe(Revision);
    }

    /// <summary>A tool result keeps <c>č</c> as <c>č</c>: nothing the model reads is an escape sequence it must retype.</summary>
    [Fact]
    public async Task A_tool_result_keeps_diacritics_quotes_and_plus_literal()
    {
        var answer = await InvokeAsync(Serving("""{"name":"p","description":"Dielňa \"U Ťava\" + ž"}"""), "get_descriptor", []);

        answer.ShouldContain("Dielňa");
        answer.ShouldContain("+ ž");
        answer.ShouldNotContain("\\u");
    }

    /// <summary>
    /// A patched proposal differs from the applied descriptor only where the patch wrote.
    /// </summary>
    /// <remarks>
    /// The property the patch exists for: the model never re-emits text it did not change, so Slovak and CEL it
    /// never touched cannot come back altered — measured by removing the one added member and comparing the rest.
    /// </remarks>
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

    /// <summary>
    /// A validation error is a violation at the validator's own pointer, naming the operation to fix.
    /// </summary>
    /// <remarks>
    /// The message and fix are the framework's words, verbatim; the <c>op</c> index is what lets the model change
    /// one operation instead of guessing which of several caused it.
    /// </remarks>
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

    /// <summary>
    /// A refused proposal carries the framework's own words to the card, and the draft is still remembered.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The wording is what the operator reads, and rewording it would make the sentence
    /// they see one nobody tested; remembering the draft is what lets the screen show what was refused
    /// rather than an empty panel.
    /// </remarks>
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

    /// <summary>
    /// A change written against a revision that has since moved is an answer, carrying the one that is current.
    /// </summary>
    /// <remarks>
    /// Routine rather than exotic: the agent reads a revision, patches, then proposes, and another operator's
    /// apply in between is all it takes. Nothing is dry-run, because the patch was written against a document
    /// that no longer exists.
    /// </remarks>
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

    /// <summary>
    /// A developer's change to <c>/access</c> is a violation the model can explain, not an exception that ends the turn.
    /// </summary>
    [Fact]
    public async Task An_access_change_by_a_developer_is_an_access_violation_rather_than_a_failed_turn()
    {
        var management = Serving(Descriptor);
        Refusing(management, new ManagementEscalationException());

        var violation = Violations(await InvokeAsync(management, "propose_change", Change("propose_change", AddNotes))).Single();

        violation["source"]!.GetValue<string>().ShouldBe("access");
        violation["pointer"]!.GetValue<string>().ShouldBe("/access");
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

    /// <summary>A pointer into nothing is refused before the dry run, and the fix lists the names that do exist.</summary>
    [Fact]
    public async Task A_pointer_into_nothing_is_a_patch_violation_listing_what_exists()
    {
        const string operations = """[{"op":"add","path":"/entities/bike/fields/notes","value":{"type":"text"}}]""";

        var violation = Violations(await InvokeAsync(Serving(Descriptor), "propose_change", Change("propose_change", operations))).Single();

        violation["source"]!.GetValue<string>().ShouldBe("patch");
        violation["op"]!.GetValue<int>().ShouldBe(0);
        violation["fix"]!.GetValue<string>().ShouldContain("bikes");
    }

    /// <summary>
    /// A replace of the whole document is the removed <c>validate_descriptor</c> in disguise, and is refused as such.
    /// </summary>
    [Fact]
    public async Task A_whole_document_replace_is_refused_before_the_dry_run()
    {
        var management = Serving(Descriptor);
        const string operations = """[{"op":"replace","path":"","value":{"name":"p"}}]""";

        var violation = Violations(await InvokeAsync(management, "propose_change", Change("propose_change", operations))).Single();

        violation["code"]!.GetValue<string>().ShouldBe("whole-document-replace");
        await management.DidNotReceiveWithAnyArgs().ApplyDescriptorAsync(default!, default!, Ct);
    }

    /// <summary>
    /// Operations a server stringified are read as the patch they spell (D9).
    /// </summary>
    /// <remarks>
    /// A <see cref="JsonElement"/> of kind string is what an OpenAI-compatible server that stringifies nested
    /// arguments delivers; a C# string would be parsed by the marshaller before the tool ran, and prove nothing.
    /// </remarks>
    [Fact]
    public async Task Operations_sent_as_a_json_string_are_read_as_the_patch_they_spell()
    {
        var management = Accepting(Serving(Descriptor));
        var arguments = Change("propose_change", AddNotes);
        arguments["operations"] = JsonSerializer.SerializeToElement(AddNotes);

        var outcome = JsonNode.Parse(await InvokeAsync(management, "propose_change", arguments))!;

        outcome["valid"]!.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>
    /// A violation inside an appended item names the append, although the validator reports the index it landed at.
    /// </summary>
    /// <remarks>
    /// The instructions teach <c>/-</c> for appends, and the validator never sees a <c>-</c>: it reports against the
    /// patched document. Matching the operation's written path would leave the violation with no <c>op</c>.
    /// </remarks>
    [Fact]
    public async Task A_violation_inside_an_appended_item_names_the_append()
    {
        const string operations = """
            [{"op":"add","path":"/entities/bikes/fields/notes","value":{"type":"text"}},
             {"op":"add","path":"/entities/bikes/hooks/beforeCreate/-","value":{"url":"c"}}]
            """;

        var violation = Violations(await InvokeAsync(
            RefusingAt(WithHooks, "/entities/bikes/hooks/beforeCreate/2/url"),
            "propose_change", Change("propose_change", operations))).Single();

        violation["op"]!.GetValue<int>().ShouldBe(1);
    }

    /// <summary>An append after a remove in the same array names the index it landed at, not the one it would have had.</summary>
    [Fact]
    public async Task An_append_after_a_remove_is_matched_at_its_shifted_index()
    {
        const string operations = """
            [{"op":"remove","path":"/entities/bikes/hooks/beforeCreate/0"},
             {"op":"add","path":"/entities/bikes/hooks/beforeCreate/-","value":{"url":"c"}}]
            """;

        var violation = Violations(await InvokeAsync(
            RefusingAt(WithHooks, "/entities/bikes/hooks/beforeCreate/1/url"),
            "propose_change", Change("propose_change", operations))).Single();

        violation["op"]!.GetValue<int>().ShouldBe(1);
    }

    /// <summary>
    /// A draft refused because the descriptor moved during the dry run is filed at the revision it was written against.
    /// </summary>
    /// <remarks>
    /// The draft is the patch applied to revision 7; labelling it 8 would describe a document nobody wrote and, on a
    /// later "review anyway", defeat the optimistic lock. The outcome still tells the model 8, to re-base on.
    /// </remarks>
    [Fact]
    public async Task A_draft_refused_by_a_concurrent_apply_keeps_its_own_base_revision()
    {
        var management = Serving(Descriptor);
        Refusing(management, new DescriptorConcurrencyException(Project, expectedRevision: Revision, actualRevision: Revision + 1));
        var tools = ManagementTools.For(management, Project);

        var outcome = JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes)))!;

        outcome["revision"]!.GetValue<int>().ShouldBe(Revision + 1);
        tools.Proposal.ShouldNotBeNull().ExpectedRevision.ShouldBe(Revision);
    }

    /// <summary>A proposal without a summary is refused as a request, rather than quietly run as a check.</summary>
    /// <remarks>
    /// Which tool was called decides whether a proposal is filed; a <c>"summary": null</c> a server forwarded must
    /// not turn <c>propose_change</c> into <c>check_change</c> with a "valid" answer and nothing on the card.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_proposal_without_a_summary_is_refused_and_files_nothing(string? summary)
    {
        var management = Accepting(Serving(Descriptor));
        var tools = ManagementTools.For(management, Project);
        var arguments = Change("propose_change", AddNotes);
        arguments["summary"] = summary;

        var answer = JsonNode.Parse(await InvokeAsync(tools, "propose_change", arguments))!;

        answer["error"]!.GetValue<string>().ShouldBe("invalid-request");
        answer["message"]!.GetValue<string>().ShouldContain("summary");
        tools.Proposal.ShouldBeNull();
        await management.DidNotReceiveWithAnyArgs().ApplyDescriptorAsync(default!, default!, Ct);
    }

    /// <summary>One proposal per turn: a second valid proposal replaces the first.</summary>
    [Fact]
    public async Task A_second_valid_proposal_replaces_the_first()
    {
        var tools = ManagementTools.For(Accepting(Serving(Descriptor)), Project);
        const string addColour = """[{"op":"add","path":"/entities/bikes/fields/colour","value":{"type":"string"}}]""";

        await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes));
        var second = Change("propose_change", addColour);
        second["summary"] = "Adds colour to bikes.";
        await InvokeAsync(tools, "propose_change", second);

        var proposal = tools.Proposal.ShouldNotBeNull();
        proposal.Summary.ShouldBe("Adds colour to bikes.");
        var fields = JsonNode.Parse(proposal.DescriptorJson)!["entities"]!["bikes"]!["fields"]!.AsObject();
        fields.ContainsKey("colour").ShouldBeTrue();
        fields.ContainsKey("notes").ShouldBeFalse();
    }

    /// <summary>A refused retry after a valid proposal leaves the valid one standing.</summary>
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

    /// <summary>
    /// Three identical refusals spend the budget; the same patch a fourth time is not dry-run, and says so.
    /// </summary>
    /// <remarks>
    /// The RCA's turn (spec §8.1 step 4): the fourth call was never checked, and the old message invited the model to
    /// tell the operator it was refused. The answer now says it was not checked and carries the last checked refusal.
    /// </remarks>
    [Fact]
    public async Task The_same_patch_after_three_stalled_refusals_is_answered_unchecked_with_the_last_refusal()
    {
        var management = Serving(Descriptor);
        Refusing(management, Refusal("gross"));
        var tools = ManagementTools.For(management, Project);

        var left = new List<int>();
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            left.Add(JsonNode.Parse(await InvokeAsync(tools, "check_change", Change("check_change", AddNotes)))!["attemptsLeft"]!.GetValue<int>());
        }

        var fourth = JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes)))!;

        left.ShouldBe([2, 1, 0]);
        fourth["unchecked"]!.GetValue<bool>().ShouldBeTrue();
        var budget = fourth["violations"]!.AsArray().Single()!;
        budget["source"]!.GetValue<string>().ShouldBe("budget");
        budget["message"]!.GetValue<string>().ShouldStartWith(ViolationMapping.UncheckedLead);
        budget["message"]!.GetValue<string>().ShouldContain("/entities/bikes/fields/gross: No.");
        await management.ReceivedWithAnyArgs(3).ApplyDescriptorAsync(default!, default!, Ct);
    }

    /// <summary>A refusal that fixed something spends no attempt, so a model converging on a fix reaches it.</summary>
    [Fact]
    public async Task Refusals_that_make_progress_spend_no_attempt_and_a_valid_fourth_is_filed()
    {
        var management = Serving(Descriptor);
        var answers = new Queue<string[]>(_progressingRefusals);
        management.ApplyDescriptorAsync(Project, Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Answer(answers.Dequeue()));
        var tools = ManagementTools.For(management, Project);

        var left = new List<int>();
        foreach (var field in _fourFields.Take(3))
        {
            left.Add(JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", Adding(field))))!["attemptsLeft"]!.GetValue<int>());
        }

        var fourth = JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", Adding(_fourFields[3]))))!;

        left.ShouldBe([2, 2, 2]);
        fourth["valid"]!.GetValue<bool>().ShouldBeTrue();
        tools.Proposal!.Refusals.ShouldBeEmpty();
    }

    /// <summary>With the three attempts spent, a patch not yet checked is still dry-run, and a valid one is filed.</summary>
    [Fact]
    public async Task A_new_patch_after_the_attempts_are_spent_is_still_dry_run()
    {
        var management = SpentOnNotesAcceptingColour();
        var tools = ManagementTools.For(management, Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes));
        }

        var fresh = JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", Adding("colour"))))!;

        fresh["valid"]!.GetValue<bool>().ShouldBeTrue();
        fresh["unchecked"].ShouldBeNull();
        tools.Proposal!.Refusals.ShouldBeEmpty();
    }

    /// <summary>
    /// With the three attempts spent, a patch that was checked <em>valid</em> is dry-run again when it is proposed,
    /// and filed: only an already refused patch is answered unchecked.
    /// </summary>
    /// <remarks>
    /// The natural sequence a model follows — ask "would this work?", then propose the same patch — must not be
    /// punished by the budget: remembering valid patches would answer the proposal unchecked and file nothing.
    /// </remarks>
    [Fact]
    public async Task A_patch_checked_valid_after_the_attempts_are_spent_is_filed_when_proposed()
    {
        var management = SpentOnNotesAcceptingColour();
        var tools = ManagementTools.For(management, Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", AddNotes));
        }

        await InvokeAsync(tools, "check_change", Change("check_change", Adding("colour")));
        var proposed = JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", Adding("colour"))))!;

        proposed["valid"]!.GetValue<bool>().ShouldBeTrue();
        proposed["unchecked"].ShouldBeNull();
        JsonNode.Parse(tools.Proposal!.DescriptorJson)!["entities"]!["bikes"]!["fields"]!["colour"].ShouldNotBeNull();
    }

    /// <summary>Six refusals end the turn's dry runs even when every one of them changed something (D41's ceiling).</summary>
    [Fact]
    public async Task Six_refusals_are_the_most_a_turn_dry_runs_even_when_each_made_progress()
    {
        var management = Serving(Descriptor);
        var calls = 0;
        management.ApplyDescriptorAsync(Project, Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Throws(_ => Refusal(calls++ % 2 == 0 ? "a" : "b"));
        var tools = ManagementTools.For(management, Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumRefusals; attempt++)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", Adding(string.Create(CultureInfo.InvariantCulture, $"f{attempt}"))));
        }

        var seventh = JsonNode.Parse(await InvokeAsync(tools, "check_change", Change("check_change", Adding("f6"))))!;

        seventh["unchecked"]!.GetValue<bool>().ShouldBeTrue();
        await management.ReceivedWithAnyArgs(ManagementTools.MaximumRefusals).ApplyDescriptorAsync(default!, default!, Ct);
    }

    /// <summary>The budget answer ends each quoted refusal with one full stop, never two.</summary>
    [Fact]
    public async Task The_budget_answer_quotes_a_refusal_without_a_double_full_stop()
    {
        var message = await BudgetMessageAfterStalls(Refusal("gross"), AddNotes);

        message.ShouldContain("/entities/bikes/fields/gross: No. ");
        message.ShouldNotContain("..");
    }

    /// <summary>A re-sent, already refused patch quotes its own refusal, not whichever patch was refused last.</summary>
    [Fact]
    public async Task A_resent_refused_patch_quotes_its_own_refusal()
    {
        var management = Serving(Descriptor);
        management.ApplyDescriptorAsync(Project, Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Answer(RefusedFor(call.Arg<ManagementApplyRequest>().DescriptorJson)));
        var tools = ManagementTools.For(management, Project);
        foreach (var field in _growingRefusals.Keys)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", Adding(field)));
        }

        var message = Violations(await InvokeAsync(tools, "check_change", Change("check_change", Adding("q")))).Single()["message"]!.GetValue<string>();

        message.ShouldContain("/entities/bikes/fields/gross: No.");
        message.ShouldNotContain("/entities/bikes/fields/y");
        message.ShouldContain(ViolationMapping.OwnRefusalLead);
    }

    /// <summary>With a valid proposal already filed, the budget answer says so rather than steering to a failure.</summary>
    [Fact]
    public async Task A_budget_answer_after_a_filed_valid_proposal_says_it_is_filed()
    {
        var tools = ManagementTools.For(SpentOnNotesAcceptingColour(), Project);
        await InvokeAsync(tools, "propose_change", Change("propose_change", Adding("colour")));
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", AddNotes));
        }

        var message = Violations(await InvokeAsync(tools, "check_change", Change("check_change", AddNotes))).Single()["message"]!.GetValue<string>();

        message.ShouldContain(ViolationMapping.ProposalFiledNote);
        message.ShouldNotContain("Quote that refusal");
        tools.Proposal!.Refusals.ShouldBeEmpty();
    }

    /// <summary>The same patch sent as an array and as a string of JSON is one patch to the budget.</summary>
    [Fact]
    public async Task The_same_patch_as_a_string_of_json_is_remembered_as_the_same_patch()
    {
        var management = Serving(Descriptor);
        Refusing(management, Refusal("gross"));
        var tools = ManagementTools.For(management, Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", AddNotes));
        }

        var asString = new Dictionary<string, object?> { ["baseRevision"] = Revision, ["operations"] = JsonSerializer.SerializeToElement(AddNotes) };
        var fourth = JsonNode.Parse(await InvokeAsync(tools, "check_change", asString))!;

        fourth["unchecked"]!.GetValue<bool>().ShouldBeTrue();
        await management.ReceivedWithAnyArgs(3).ApplyDescriptorAsync(default!, default!, Ct);
    }

    /// <summary>A stale patch re-sent after three stalls is answered unchecked, quoting its stale-revision refusal.</summary>
    [Fact]
    public async Task A_stale_patch_resent_after_three_stalls_quotes_its_stale_revision_refusal()
    {
        var tools = ManagementTools.For(Serving(Descriptor), Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", AddNotes, baseRevision: 1));
        }

        var outcome = JsonNode.Parse(await InvokeAsync(tools, "check_change", Change("check_change", AddNotes, baseRevision: 1)))!;
        var message = outcome["violations"]![0]!["message"]!.GetValue<string>();

        outcome["unchecked"]!.GetValue<bool>().ShouldBeTrue();
        message.ShouldContain("The descriptor is at revision 7; this change was written against revision 1.");
        message.ShouldNotContain("..");
    }

    /// <summary>A refusal that carried only warnings leaves nothing to quote, and the answer says so, not "said: .".</summary>
    [Fact]
    public async Task A_refusal_with_no_blocking_violation_is_not_quoted_as_an_empty_sentence()
    {
        var onlyWarning = new DescriptorValidationException(new DescriptorValidationResult(
            [new DescriptorValidationError("/entities/bikes", "Careful.", null, DescriptorValidationSeverity.Warning)]));

        var message = await BudgetMessageAfterStalls(onlyWarning, AddNotes);

        message.ShouldContain(ViolationMapping.NothingToQuoteNote);
        message.ShouldNotContain(": .");
        message.ShouldNotContain("..");
    }

    /// <summary>A spent budget still reports the revision the descriptor is at, never the one the model claimed.</summary>
    [Fact]
    public async Task A_spent_budget_reports_the_revision_the_descriptor_is_at_rather_than_the_claimed_base()
    {
        var tools = ManagementTools.For(Serving(Descriptor), Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", AddNotes, baseRevision: 1));
        }

        var outcome = JsonNode.Parse(await InvokeAsync(tools, "check_change", Change("check_change", AddNotes, baseRevision: 1)))!;

        outcome["violations"]![0]!["source"]!.GetValue<string>().ShouldBe("budget");
        outcome["revision"]!.GetValue<int>().ShouldBe(Revision);
    }

    /// <summary>A "would this work?" question is answered, and files nothing.</summary>
    [Fact]
    public async Task Check_change_files_no_proposal()
    {
        var tools = ManagementTools.For(Accepting(Serving(Descriptor)), Project);

        await InvokeAsync(tools, "check_change", Change("check_change", AddNotes));

        tools.Proposal.ShouldBeNull();
    }

    /// <summary>A turn that only read something proposes nothing.</summary>
    [Fact]
    public async Task A_read_only_turn_leaves_no_proposal_behind()
    {
        var tools = ManagementTools.For(Serving(Descriptor), Project);

        await InvokeAsync(tools, "get_descriptor", []);

        tools.Proposal.ShouldBeNull();
    }

    /// <summary>A refused caller is something to tell the operator, not a stack trace that ends the turn.</summary>
    [Fact]
    public async Task A_forbidden_caller_is_reported_to_the_model_rather_than_crashing_the_turn()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetSchemaAsync(Project, Arg.Any<CancellationToken>()).Throws(new ManagementForbiddenException());

        (await InvokeAsync(management, "get_schema", [])).ShouldContain("forbidden");
    }

    /// <summary>And a read that raced an apply reports it rather than ending the turn.</summary>
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

    private static IAlvoManagement RefusingAt(string descriptorJson, string pointer)
    {
        var management = Serving(descriptorJson);
        Refusing(management, new DescriptorValidationException(new DescriptorValidationResult(
            [new DescriptorValidationError(pointer, "No.", null, DescriptorValidationSeverity.Error)])));

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

    private static readonly string[][] _progressingRefusals = [["a", "b"], ["a"], ["c"], []];
    private static readonly string[] _fourFields = ["one", "two", "three", "four"];

    /// <summary>A refusal at one field of <c>bikes</c> per name — the blocking set a test controls.</summary>
    private static DescriptorValidationException Refusal(params string[] fields) =>
        new(new DescriptorValidationResult(
            [.. fields.Select(field => new DescriptorValidationError("/entities/bikes/fields/" + field, "No.", null, DescriptorValidationSeverity.Error))]));

    /// <summary>The dry run's answer: valid when nothing is refused, else the refusal.</summary>
    private static ManagementApplyResult Answer(string[] refused) =>
        refused.Length == 0 ? new ManagementApplyResult(Applied: false, Revision, EmptyPlan) : throw Refusal(refused);

    /// <summary>A one-operation patch adding a text field — built, not written as a raw string, so no brace run closes it.</summary>
    private static string Adding(string field) =>
        new JsonArray(new JsonObject
        {
            ["op"] = "add",
            ["path"] = "/entities/bikes/fields/" + field,
            ["value"] = new JsonObject { ["type"] = "text" },
        }).ToJsonString();

    /// <summary>Refuses every patch at <c>gross</c> except one adding <c>colour</c>, which it accepts.</summary>
    private static IAlvoManagement SpentOnNotesAcceptingColour()
    {
        var management = Serving(Descriptor);
        management.ApplyDescriptorAsync(Project, Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Answer(call.Arg<ManagementApplyRequest>().DescriptorJson.Contains("\"colour\"", StringComparison.Ordinal) ? [] : ["gross"]));

        return management;
    }

    /// <summary>Each field's patch is refused at a growing set, so every refusal stalls: q, then r, then s.</summary>
    private static readonly Dictionary<string, string[]> _growingRefusals = new(StringComparer.Ordinal)
    {
        ["q"] = ["gross"],
        ["r"] = ["gross", "x"],
        ["s"] = ["gross", "x", "y"],
    };

    private static string[] RefusedFor(string descriptorJson) =>
        _growingRefusals.First(pair => descriptorJson.Contains($"\"{pair.Key}\"", StringComparison.Ordinal)).Value;

    /// <summary>The budget answer's message for <paramref name="operations"/> re-sent after three stalled refusals.</summary>
    private static async Task<string> BudgetMessageAfterStalls(Exception refusal, string operations)
    {
        var management = Serving(Descriptor);
        Refusing(management, refusal);
        var tools = ManagementTools.For(management, Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", operations));
        }

        return Violations(await InvokeAsync(tools, "check_change", Change("check_change", operations))).Single()["message"]!.GetValue<string>();
    }

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
