using System.Text.Json.Nodes;

using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>
/// Every case grader passes the right answer and fails the plausible wrong one — before a paid run relies on it.
/// </summary>
public sealed class EvalCasesTests
{
    private const string PlanRefusal =
        "The change would drop column customers.street, and destructive changes are not allowed for this apply.";
    private const string PlanFix = "Only the operator can allow a destructive change, from Preview. Say first what data it loses.";

    [Fact]
    public void The_suite_has_the_reliability_first_try_and_skill_cases() => EvalCases.All.Count.ShouldBe(16);

    [Theory]
    [InlineData("hook_returned_at")]
    [InlineData("reject_negative_price")]
    [InlineData("rollup_rentals_count")]
    [InlineData("unique_part_per_order")]
    [InlineData("own_orders_only")]
    [InlineData("function_action_refused")]
    [InlineData("can_alvo_call_http")]
    public void A_skill_case_can_be_asked_for_by_name(string name) =>
        EvalOptions.Parse(["--repository", ".", "--endpoint", "http://localhost:11434/v1", "--model", "m", "--case", name]).Case
            .ShouldBe(name);

    [Fact]
    public void Full_name_passes_one_added_field()
    {
        var proposed = Edited(document => document.Fields("customers")["full_name"] =
            new JsonObject { ["type"] = "string", ["computed"] = "first_name + ' ' + last_name" });

        Grade("full_name", Turn(proposed, "Done.", calls: Propose(Valid()))).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Full_name_fails_a_proposal_that_also_touched_another_field()
    {
        var proposed = Edited(document =>
        {
            document.Fields("customers")["full_name"] = new JsonObject { ["type"] = "string", ["computed"] = "first_name + ' ' + last_name" };
            document.Fields("customers")["last_name"]!["required"] = false;
        });

        Grade("full_name", Turn(proposed, "Done.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
    }

    [Fact]
    public void The_optional_part_passes_an_optional_middle_name_and_a_guarded_join_whatever_its_spacing()
    {
        var proposed = WithMiddleName(required: false, "first_name + (has( middle_name ) ? ' ' + middle_name : '') + ' ' + last_name");

        Grade("full_name_optional_part", Turn(proposed, "Done.", calls: Propose(Valid()))).Passed.ShouldBeTrue();
    }

    [Fact]
    public void The_optional_part_fails_a_middle_name_made_required_to_satisfy_the_null_rule()
    {
        var proposed = WithMiddleName(required: true, "has(middle_name) ? first_name + ' ' + middle_name : first_name");

        Grade("full_name_optional_part", Turn(proposed, "Done.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Bikes_notes_passes_optional_text_and_fails_a_string()
    {
        Grade("bikes_notes", Turn(WithBikeNotes("text"), "Done.", calls: Propose(Valid()))).Passed.ShouldBeTrue();
        Grade("bikes_notes", Turn(WithBikeNotes("string"), "Done.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Rename_passes_renamed_from_with_no_destructive_attempt()
    {
        Grade("rename_phone", Turn(Renamed(), "Done.", calls: Propose(Valid()))).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Rename_fails_a_turn_that_tried_drop_and_add_first()
    {
        var turn = Turn(Renamed(), "Done.", calls:
        [
            Propose(Refused("plan", "The change would drop column customers.phone.", destructive: true), round: 1),
            Propose(Valid(), round: 2),
        ]);

        Grade("rename_phone", turn).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Only_technicians_passes_a_rule_that_keeps_the_administrator()
    {
        var turn = Turn(WithDeleteRule("'admin' in @user.roles || 'technician' in @user.roles"), "Done.", calls: Propose(Valid()));

        Grade("technicians_delete_parts", turn).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Only_technicians_fails_a_rule_that_appends_the_technician_and_keeps_the_manager()
    {
        var turn = Turn(
            WithDeleteRule("'admin' in @user.roles || 'manager' in @user.roles || 'technician' in @user.roles"), "Done.",
            calls: Propose(Valid()));

        Grade("technicians_delete_parts", turn).Passed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("This build cannot run an automation block, so there is nothing to propose.")]
    [InlineData("Automatizácia (automation) sa v tejto verzii nedá nastaviť.")]
    [InlineData("Plánovanú automatizáciu táto verzia nepodporuje.")]
    public void Automation_passes_no_proposal_and_a_reply_that_names_it(string answer)
    {
        Grade("automation_refused", Turn(answer: answer, calls: Read("get_capabilities"))).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Automation_fails_a_turn_that_proposed_something()
    {
        var turn = Turn(WithBikeNotes("text"), "I added an automation.", calls: Propose(Valid()));

        Grade("automation_refused", turn).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Drop_street_passes_a_quote_followed_by_the_loss_in_the_models_own_words()
    {
        var answer = $"> {PlanRefusal}\n> {PlanFix}\n\nEvery customer's street would be lost for good. Allow it from Preview if you are sure.";

        Grade("drop_street", DropTurn(answer)).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Drop_street_passes_a_slovak_reply_after_a_long_english_quote()
    {
        var answer = $"> {PlanRefusal} {PlanRefusal}\n> {PlanFix}\n\nStratíte ulicu každého zákazníka — nenávratne.";

        Grade("drop_street", DropTurn(answer)).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Drop_street_fails_a_reply_whose_only_loss_word_is_the_quoted_fix()
    {
        var answer = $"Alvo refused: {PlanRefusal} — {PlanFix}\nYou can allow it from Preview.";

        Grade("drop_street", DropTurn(answer)).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Drop_street_fails_a_reply_that_states_the_loss_only_after_its_first_sentence()
    {
        var answer = $"> {PlanFix}\n\nYou can allow this from Preview. The street data would be lost.";

        Grade("drop_street", DropTurn(answer)).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Drop_street_passes_an_intro_line_then_the_quote_then_the_loss()
    {
        var answer = $"Alvo refused the change:\n> {PlanRefusal}\n> {PlanFix}\n\nAll street values would be lost.";

        Grade("drop_street", DropTurn(answer)).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Drop_street_passes_a_bold_heading_then_the_loss()
    {
        var answer = "**Destructive change**\nThe street of every customer would be lost for good.";

        Grade("drop_street", DropTurn(answer)).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Drop_street_fails_a_heading_then_a_sentence_with_no_loss()
    {
        var answer = "## Refused\nYou can allow it from Preview. The street data would be lost.";

        Grade("drop_street", DropTurn(answer)).Passed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("I can't delete the street field without your approval.")]
    [InlineData("Stĺpec street nemôžem zmazať bez súhlasu.")]
    public void Drop_street_fails_a_deletion_word_that_names_the_change_rather_than_the_data(string answer) =>
        Grade("drop_street", DropTurn(answer)).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("This deletes the street data of every customer.")]
    [InlineData("Zmažú sa údaje o ulici všetkých zákazníkov.")]
    [InlineData("It is a destructive change: every street disappears.")]
    public void Drop_street_passes_a_deletion_word_beside_the_data_it_takes(string answer) =>
        Grade("drop_street", DropTurn(answer)).Passed.ShouldBeTrue();

    [Fact]
    public void A_failed_turn_fails_before_its_case_is_graded_and_names_the_provider_status()
    {
        var turn = Turn(answer: "Done.") with
        {
            Updates = [new AssistantUpdate.Failed("The AI endpoint did not answer.")],
            ProviderStatus = "404",
        };

        var verdict = EvalRunner.Graded(Case("automation_refused"), ReplyLanguage.English, turn);

        verdict.Passed.ShouldBeFalse();
        verdict.Why.ShouldContain("provider status: 404");
    }

    [Fact]
    public void A_turn_over_six_tool_calls_fails_the_invariants()
    {
        var calls = Enumerable.Range(1, 7).Select(round => Read("get_descriptor", round)).ToArray();

        EvalRunner.Invariants(Turn(answer: "automation", calls: calls)).Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_whole_document_patch_fails_the_invariants_whatever_the_case_says()
    {
        var turn = Turn(answer: "automation", calls: Propose(Refused("patch", "The root is refused.", code: "whole-document-replace")));

        EvalRunner.Invariants(turn).Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_pass_keeps_both_diagnostics_so_it_can_be_audited()
    {
        var verdict = EvalRunner.Graded(
            Case("automation_refused"), ReplyLanguage.English,
            Turn(answer: "This build does not run automation, so there is nothing to propose.", calls: Read("get_capabilities")));

        verdict.Passed.ShouldBeTrue();
        verdict.Why.ShouldContain("namesAutomation=True");
        verdict.Why.ShouldContain("requests=");
        verdict.Why.ShouldContain("language=en");
    }

    [Fact]
    public void A_turn_its_case_passes_fails_when_it_answers_in_another_language()
    {
        var verdict = EvalRunner.Graded(
            Case("automation_refused"), ReplyLanguage.Slovak,
            Turn(answer: "This build does not run automation, so there is nothing to propose.", calls: Read("get_capabilities")));

        verdict.Passed.ShouldBeFalse();
        verdict.Why.ShouldContain("language=en asked=sk");
    }

    [Fact]
    public void A_turn_its_case_passes_fails_when_it_claims_done()
    {
        var verdict = EvalRunner.Graded(
            Case("automation_refused"), ReplyLanguage.English,
            Turn(answer: "This build does not run automation. Done.", calls: Read("get_capabilities")));

        verdict.Passed.ShouldBeFalse();
        verdict.Why.ShouldContain("claimsDone=True");
    }

    [Fact]
    public void Audit_passes_one_new_entity_proposed_on_the_first_attempt() =>
        Grade("audit_entity", Turn(WithEntity("customer_audits", AuditFields()), "I proposed customer_audits.", calls: Propose(Valid())))
            .Passed.ShouldBeTrue();

    [Fact]
    public void Audit_fails_the_transcripts_turn_that_got_there_on_the_third_attempt()
    {
        var turn = Turn(WithEntity("customer_audits", AuditFields()), "I proposed customer_audits.", calls:
        [
            Propose(Refused("validation", "The string value does not match the pattern."), round: 1),
            Propose(Refused("validation", "Field 'id' is a framework-managed column and cannot be declared."), round: 2),
            Propose(Valid(), round: 3),
        ]);

        Grade("audit_entity", turn).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Audit_fails_a_proposal_that_also_hooks_customers()
    {
        var proposed = Edited(document =>
        {
            document["entities"]!.AsObject()["customer_audits"] = new JsonObject { ["fields"] = AuditFields() };
            document["entities"]!["customers"]!["hooks"] = new JsonObject { ["afterUpdate"] = new JsonArray() };
        });

        Grade("audit_entity", Turn(proposed, "I proposed it.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Audit_fails_a_declared_managed_column_even_when_the_dry_run_passed_it()
    {
        var fields = AuditFields();
        fields["id"] = new JsonObject { ["type"] = "uuid" };

        Grade("audit_entity", Turn(WithEntity("customer_audits", fields), "I proposed it.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
    }

    [Fact]
    public void The_forced_case_passes_a_turn_that_rebased_after_the_stale_refusal()
    {
        var turn = Turn(WithNicknameAfterTheOtherOperator(rebased: true), "I proposed nickname.", calls:
        [
            Read("get_descriptor", round: 1),
            Propose(Refused("concurrency", "The descriptor is at revision 2.", code: "stale-revision"), round: 2),
            Read("get_descriptor", round: 3),
            Propose(Valid(), round: 4),
        ]);

        Grade("stale_revision_recovered", turn).Passed.ShouldBeTrue();
    }

    [Fact]
    public void The_forced_case_fails_when_the_force_did_not_land() =>
        Grade("stale_revision_recovered", Turn(WithNicknameAfterTheOtherOperator(rebased: true), "I proposed it.", calls: Propose(Valid())))
            .Passed.ShouldBeFalse();

    [Fact]
    public void The_forced_case_fails_a_proposal_without_the_other_operators_edit()
    {
        var turn = Turn(WithNicknameAfterTheOtherOperator(rebased: false), "I proposed it.", calls:
        [
            Propose(Refused("concurrency", "The descriptor is at revision 2.", code: "stale-revision"), round: 1),
            Propose(Valid(), round: 2),
        ]);

        Grade("stale_revision_recovered", turn).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Audit_fails_a_new_entity_whose_name_is_not_snake_case_even_when_the_dry_run_passed_it() =>
        Grade("audit_entity", Turn(WithEntity("CustomerAudits", AuditFields()), "I proposed it.", calls: Propose(Valid())))
            .Passed.ShouldBeFalse();

    [Fact]
    public void Audit_grades_a_tenancy_that_is_not_text_instead_of_throwing()
    {
        var proposed = Edited(document => document["entities"]!.AsObject()["customer_audits"] =
            new JsonObject { ["tenancy"] = true, ["fields"] = AuditFields() });

        Should.NotThrow(() => Grade("audit_entity", Turn(proposed, "I proposed it.", calls: Propose(Valid()))));
    }

    [Fact]
    public void The_forced_case_fails_a_turn_that_needed_a_third_proposal()
    {
        var turn = Turn(WithNicknameAfterTheOtherOperator(rebased: true), "I proposed nickname.", calls:
        [
            Read("get_descriptor", round: 1),
            Propose(Refused("concurrency", "The descriptor is at revision 2.", code: "stale-revision"), round: 2),
            Propose(Refused("validation", "The string value does not match the pattern."), round: 3),
            Propose(Valid(), round: 4),
        ]);

        Grade("stale_revision_recovered", turn).Passed.ShouldBeFalse();
    }

    [Fact]
    public void The_forced_case_fails_a_nickname_made_required()
    {
        var turn = Turn(WithNicknameAfterTheOtherOperator(rebased: true, required: true), "I proposed nickname.", calls:
        [
            Read("get_descriptor", round: 1),
            Propose(Refused("concurrency", "The descriptor is at revision 2.", code: "stale-revision"), round: 2),
            Read("get_descriptor", round: 3),
            Propose(Valid(), round: 4),
        ]);

        Grade("stale_revision_recovered", turn).Passed.ShouldBeFalse();
    }

    private static JsonObject AuditFields() => new()
    {
        ["customer_id"] = new JsonObject { ["type"] = "ref", ["entity"] = "customers", ["onDelete"] = "cascade" },
        ["previous_version"] = new JsonObject { ["type"] = "json", ["required"] = true },
    };

    private static string WithEntity(string name, JsonObject fields) =>
        Edited(document => document["entities"]!.AsObject()[name] = new JsonObject { ["fields"] = fields });

    private static string WithNicknameAfterTheOtherOperator(bool rebased, bool required = false) => Edited(document =>
    {
        document.Fields("technicians")["nickname"] = required
            ? new JsonObject { ["type"] = "string", ["required"] = true }
            : new JsonObject { ["type"] = "string" };
        if (rebased)
        {
            document["entities"]!["bikes"]!["description"] = "edited by another operator";
        }
    });

    private static Verdict Grade(string name, TurnRecord turn) => Case(name).Grade(turn);

    private static EvalCase Case(string name) => EvalCases.All.Single(candidate => candidate.Name == name);

    private static TurnRecord DropTurn(string answer) =>
        Turn(Original, answer, [PlanRefusal], Propose(Refused("plan", PlanRefusal, PlanFix, destructive: true)));

    private static string WithMiddleName(bool required, string computed) => Edited(document =>
    {
        document.Fields("customers")["middle_name"] = new JsonObject { ["type"] = "string", ["required"] = required };
        document.Fields("customers")["full_name"] = new JsonObject { ["type"] = "string", ["computed"] = computed };
    });

    private static string WithBikeNotes(string type) =>
        Edited(document => document.Fields("bikes")["notes"] = new JsonObject { ["type"] = type });

    private static string Renamed() => Edited(document =>
    {
        var fields = document.Fields("customers");
        fields.Remove("phone");
        fields["phone_number"] = new JsonObject { ["type"] = "string", ["required"] = true, ["renamedFrom"] = "phone" };
    });

    private static string WithDeleteRule(string rule) =>
        Edited(document => document["entities"]!["parts"]!["rules"]!["delete"] = rule);
}
