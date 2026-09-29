using System.Text.Json.Nodes;

using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>A proposal touching an area passes only when that area's skill was loaded before the first dry run (D31, D40).</summary>
public sealed class SkillsReadTests
{
    private const string EntitiesAndFields = "alvo-descriptor-entities-and-fields";
    private const string NotesProposed = "I proposed an optional notes field on bikes; nothing changes until you apply it from Preview.";

    [Theory]
    [InlineData("/entities/parts/rules/delete", "rules-and-cel")]
    [InlineData("/entities/rentals/hooks/beforeUpdate", "hooks")]
    [InlineData("/entities/order_lines/indexes", "indexes")]
    [InlineData("/entities/rentals/audit", "traits-and-tenancy")]
    [InlineData("/tenancy", "traits-and-tenancy")]
    [InlineData("/access", "project-access")]
    [InlineData("/entities/bikes/fields/notes", "entities-and-fields")]
    [InlineData("/entities/order_lines/fields/line_total/computed", "computed-and-rollups")]
    public void A_changed_path_belongs_to_its_area(string path, string area) =>
        SkillsRead.AreaOf(path, proposed: null).ShouldBe(area);

    [Fact]
    public void A_new_field_that_is_computed_or_a_rollup_belongs_to_computed_and_rollups()
    {
        SkillsRead.AreaOf("/entities/customers/fields/full_name", new JsonObject { ["computed"] = "first_name" }).ShouldBe("computed-and-rollups");
        SkillsRead.AreaOf("/entities/customers/fields/rentals_count", new JsonObject { ["rollup"] = new JsonObject() }).ShouldBe("computed-and-rollups");
    }

    [Fact]
    public void A_changed_scalar_facet_belongs_to_its_entitys_fields_without_throwing() =>
        SkillsRead.AreaOf("/entities/customers/fields/street/maxLength", JsonValue.Create(200)).ShouldBe("entities-and-fields");

    [Fact]
    public void A_path_outside_every_area_needs_no_skill() =>
        SkillsRead.AreaOf("/formats/iban", JsonValue.Create("x")).ShouldBeNull();

    [Fact]
    public void Every_area_the_grader_can_name_is_a_skill_in_the_catalogue() =>
        SkillsRead.Areas.Select(area => "alvo-descriptor-" + area).ShouldAllBe(skill => SkillsRead.Catalogue.Contains(skill));

    [Fact]
    public void A_proposal_after_loading_its_skill_passes() =>
        SkillsRead.Judge(NotesTurn(Loads(EntitiesAndFields, round: 1), Propose(Valid(), round: 2))).Passed.ShouldBeTrue();

    [Fact]
    public void A_proposal_whose_skill_was_loaded_in_the_same_round_fails() =>
        SkillsRead.Judge(NotesTurn(Loads(EntitiesAndFields, round: 1), Propose(Valid(), round: 1))).Passed.ShouldBeFalse();

    [Fact]
    public void A_skill_loaded_only_after_a_first_refused_dry_run_fails()
    {
        var turn = NotesTurn(
            Propose(Refused("validation", "The field is refused."), round: 1), Loads(EntitiesAndFields, round: 2), Propose(Valid(), round: 3));

        SkillsRead.Judge(turn).Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_proposal_with_no_skill_loaded_fails_and_says_which_was_needed()
    {
        var verdict = SkillsRead.Judge(Turn(
            Edited(document => document["entities"]!["parts"]!["rules"]!["delete"] = "'technician' in @user.roles"),
            "I proposed it.", calls: Propose(Valid())));

        verdict.Passed.ShouldBeFalse();
        verdict.Why.ShouldContain("alvo-descriptor-rules-and-cel");
    }

    [Fact]
    public void A_turn_with_no_proposal_needs_no_skill() =>
        SkillsRead.Judge(Turn(answer: "This build does not run automation.")).Passed.ShouldBeTrue();

    [Fact]
    public void Skill_reads_do_not_count_against_the_six_management_calls()
    {
        RecordedCall[] calls =
        [
            Read("get_descriptor", 1), Loads(EntitiesAndFields, 1), Loads("alvo-descriptor-hooks", 1),
            Read("get_schema", 2), Read("get_capabilities", 2), Read("get_revisions", 2),
            Propose(Refused("validation", "Refused."), 3), Propose(Valid(), 4),
        ];
        var turn = Turn(answer: "ok", calls: calls);

        turn.ManagementCalls.ShouldBe(6);
        turn.SkillReads.ShouldBe(2);
        EvalRunner.Invariants(turn).Passed.ShouldBeTrue();
    }

    [Fact]
    public void More_than_four_skill_reads_fail_the_invariants()
    {
        var calls = Enumerable.Range(1, 5).Select(round => Loads("alvo-descriptor-hooks", round)).ToArray();

        EvalRunner.Invariants(Turn(answer: "ok", calls: calls)).Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_graded_proposing_turn_that_loaded_its_skill_passes_and_says_what_it_needed()
    {
        var verdict = EvalRunner.Graded(Case("bikes_notes"), ReplyLanguage.English, NotesTurn(Loads(EntitiesAndFields, 1), Propose(Valid(), 2)));

        verdict.Passed.ShouldBeTrue(verdict.Why);
        verdict.Why.ShouldContain($"skillsNeeded=[{EntitiesAndFields}]");
    }

    [Fact]
    public void A_graded_proposing_turn_its_case_passes_fails_when_it_loaded_no_skill()
    {
        var verdict = EvalRunner.Graded(Case("bikes_notes"), ReplyLanguage.English, NotesTurn(Propose(Valid())));

        verdict.Passed.ShouldBeFalse();
        verdict.Why.ShouldContain($"skillsNeeded=[{EntitiesAndFields}] skillsLoaded=[]");
    }

    private static TurnRecord NotesTurn(params RecordedCall[] calls) =>
        Turn(Edited(document => document.Fields("bikes")["notes"] = new JsonObject { ["type"] = "text" }), NotesProposed, calls: calls);

    private static EvalCase Case(string name) => EvalCases.All.Single(candidate => candidate.Name == name);
}
