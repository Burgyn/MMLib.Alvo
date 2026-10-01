using MMLib.Alvo.Ai.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;

using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>
/// The RCA 2 case, <c>task_management_workers</c> (spec §9, D53): its right answer passes, and each clause of its grader
/// has a canned turn that fails that clause alone — so removing a clause turns a fact red.
/// </summary>
/// <remarks>
/// The right turn is <see cref="SkillCaseAnswers.TaskManagement"/> applied to <see cref="Turns.Original"/>;
/// <c>SkillCaseAnswerTests</c> (Host.Tests) dry-runs the same patch through the real validator, valid with no warning.
/// </remarks>
public sealed class TaskManagementCaseTests
{
    private const string Name = "task_management_workers";
    private const string Proposed = "I proposed tasks and task comments. Nothing changes until you apply it from Preview.";
    private const string Entities = "alvo-descriptor-entities-and-fields";
    private const string Rules = "alvo-descriptor-rules-and-cel";
    private const string Traits = "alvo-descriptor-traits-and-tenancy";

    private static readonly string[] _allSkills = [Entities, Rules, Traits];

    [Fact]
    public void The_right_answer_passes_its_case() =>
        Grade(Right()).Passed.ShouldBeTrue(Grade(Right()).Why);

    [Fact]
    public void The_right_answer_passes_the_whole_grading()
    {
        var verdict = EvalRunner.Graded(Case, ReplyLanguage.English, Right());

        verdict.Passed.ShouldBeTrue(verdict.Why);
    }

    [Fact]
    public void A_refused_proposal_fails() =>
        Fails(Turn(Answer(), Proposed, refusals: ["refused"], calls: [.. AllLoads(), Read("get_descriptor", 1), Propose(Refused("validation", "Refused."), 2)]), "valid=False");

    [Fact]
    public void Fewer_than_two_new_entities_fail() =>
        Fails(Right(document => document["entities"]!.AsObject().Remove("task_comments")), "newEntities=1");

    [Fact]
    public void A_changed_path_that_is_not_a_new_entity_fails() =>
        Fails(Right(document => document.Fields("bikes")["notes"] = new JsonObject { ["type"] = "text" }), "onlyNewEntities=False");

    [Fact]
    public void A_missing_link_fails() =>
        Fails(Right(document => document.Fields("tasks").Remove("part_id")), "links=False");

    /// <summary>
    /// The RCA 1 defect: a ref to an entity the project does not declare. <c>part_id</c> still refs <c>parts</c>, so the
    /// links clause holds and only this one fails (pre-flight M2).
    /// </summary>
    [Fact]
    public void A_ref_to_an_undeclared_entity_fails() =>
        Fails(
            Right(document => document.Fields("tasks")["product_id"] = new JsonObject { ["type"] = "ref", ["entity"] = "products" }),
            "undeclared=[products]");

    [Fact]
    public void A_declared_managed_column_fails() =>
        Fails(Right(document => document.Fields("tasks")["created_at"] = new JsonObject { ["type"] = "datetime" }), "managed=[tasks.created_at]");

    [Fact]
    public void A_rule_comparing_the_caller_with_another_entitys_ref_fails() =>
        Fails(
            Right(document => document["entities"]!["tasks"]!["rules"]!["update"] =
                "'admin' in @user.roles || 'manager' in @user.roles || technician_id == @user.id"),
            "callerVsRef=[tasks.technician_id]");

    [Fact]
    public void A_before_hook_condition_comparing_the_caller_with_another_entitys_ref_fails() =>
        Fails(
            Right(document => document["entities"]!["tasks"]!["hooks"] = new JsonObject
            {
                ["beforeUpdate"] = new JsonArray(new JsonObject
                {
                    ["condition"] = "new.technician_id != @user.id",
                    ["action"] = new JsonObject { ["reject"] = "Only the assigned technician." },
                }),
            }),
            "callerVsRef=[tasks.technician_id]");

    [Fact]
    public void Two_refusals_fail() =>
        Fails(
            Turn(Answer(), Proposed, calls:
                [.. AllLoads(), Read("get_descriptor", 1), Propose(Refused("validation", "No."), 2), Propose(Refused("validation", "No."), 3), Propose(Valid(), 4)]),
            "refused=2");

    /// <summary>The skills are graded by <c>SkillsRead</c> on every turn, not by the case (D53): a turn missing one fails the grading.</summary>
    [Fact]
    public void The_right_answer_without_the_traits_skill_fails_the_whole_grading() =>
        EvalRunner.Graded(Case, ReplyLanguage.English, Turn(Answer(), Proposed, calls: [Loads(Entities, 1), Loads(Rules, 1), Read("get_descriptor", 1), Propose(Valid(), 2)]))
            .Passed.ShouldBeFalse();

    /// <summary>"Proposed" is graded by <c>ProposalWording</c> on every turn (D53): a reply that says it created the tables fails.</summary>
    [Fact]
    public void The_right_answer_that_says_it_created_the_tables_fails_the_whole_grading() =>
        EvalRunner.Graded(Case, ReplyLanguage.English, Turn(Answer(), "I created the tables.", calls: [.. AllLoads(), Read("get_descriptor", 1), Propose(Valid(), 2)]))
            .Passed.ShouldBeFalse();

    private static EvalCase Case => EvalCases.All.Single(candidate => candidate.Name == Name);

    private static Verdict Grade(TurnRecord turn) => Case.Grade(turn);

    /// <summary>The turn fails its case, and the grading says why: the one clause <paramref name="clause"/> names.</summary>
    private static void Fails(TurnRecord turn, string clause)
    {
        var verdict = Grade(turn);

        verdict.Passed.ShouldBeFalse(verdict.Why);
        verdict.Why.ShouldContain(clause);
    }

    /// <summary>The right turn, with <paramref name="change"/> applied to its proposal — the one wrong thing, when given.</summary>
    private static TurnRecord Right(Action<JsonObject>? change = null)
    {
        var document = JsonNode.Parse(Answer())!.AsObject();
        change?.Invoke(document);
        return Turn(document.ToJsonString(), Proposed, calls: [.. AllLoads(), Read("get_descriptor", 1), Propose(Valid(), round: 2)]);
    }

    private static RecordedCall[] AllLoads() => [.. _allSkills.Select(skill => Loads(skill, 1))];

    private static string Answer()
    {
        using var operations = JsonDocument.Parse(SkillCaseAnswers.TaskManagement);
        return DescriptorDiff.Patched(Original, operations.RootElement);
    }
}
