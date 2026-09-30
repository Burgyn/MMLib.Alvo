using MMLib.Alvo.Ai.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;

using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>
/// The seven skill cases (spec §7.5, D36): each passes its right answer, and fails that answer with exactly one thing
/// wrong — one fact per clause of its grader, so removing a clause turns a fact red. Every right answer also passes
/// the whole grading, loading the skill its proposal needs (D31).
/// </summary>
/// <remarks>
/// The proposing right answers are <see cref="SkillCaseAnswers"/>, applied to <see cref="Turns.Original"/> with the
/// assistant's own JSON Patch; <c>SkillCaseAnswerTests</c> (Host.Tests) dry-runs the same patches through the real
/// validator.
/// </remarks>
public sealed class SkillCasesTests
{
    private const string Hooks = "alvo-descriptor-hooks";
    private const string Computed = "alvo-descriptor-computed-and-rollups";
    private const string Indexes = "alvo-descriptor-indexes";
    private const string Rules = "alvo-descriptor-rules-and-cel";
    private const string Capabilities = "alvo-descriptor-capabilities-and-limits";
    private const string Proposed = "I proposed the change. Nothing changes until you apply it from Preview.";
    private const string FunctionAnswer =
        "> The 'function' action is declared in the schema but not implemented in this build.\n\n"
        + "This build does not run functions, so I did not propose a hook for the invoicing function.";
    private const string HttpAnswer =
        "No, not in this build: an after-hook http.call is refused. A webhook action posts to a declared endpoint, but it is not signed.";

    private static readonly Dictionary<string, string> _skillOf = new(StringComparer.Ordinal)
    {
        ["hook_returned_at"] = Hooks,
        ["reject_negative_price"] = Hooks,
        ["rollup_rentals_count"] = Computed,
        ["unique_part_per_order"] = Indexes,
        ["own_orders_only"] = Rules,
    };

    public static TheoryData<string> AllCases() =>
        ["hook_returned_at", "reject_negative_price", "rollup_rentals_count", "unique_part_per_order", "own_orders_only", "function_action_refused", "can_alvo_call_http"];

    public static TheoryData<string> ProposingCases() => [.. SkillCaseAnswers.RightAnswers.Keys];

    [Theory]
    [MemberData(nameof(AllCases))]
    public void A_right_answer_passes_its_case(string name) =>
        Grade(name, RightAnswer(name)).Passed.ShouldBeTrue(Grade(name, RightAnswer(name)).Why);

    [Theory]
    [MemberData(nameof(AllCases))]
    public void A_right_answer_passes_the_whole_grading_with_the_skill_it_needs(string name)
    {
        var verdict = EvalRunner.Graded(Case(name), ReplyLanguage.English, RightAnswer(name));

        verdict.Passed.ShouldBeTrue(verdict.Why);
    }

    [Theory]
    [MemberData(nameof(ProposingCases))]
    public void A_right_proposal_whose_dry_run_was_refused_fails(string name) =>
        Grade(name, Turn(Answer(name), Proposed, refusals: ["refused"], calls: [Loads(_skillOf[name], 1), Propose(Refused("validation", "Refused."), 2)]))
            .Passed.ShouldBeFalse();

    [Theory]
    [MemberData(nameof(ProposingCases))]
    public void A_right_proposal_beside_an_unrelated_change_fails(string name) =>
        Grade(name, Proposing(name, document => document.Fields("bikes")["notes"] = new JsonObject { ["type"] = "text" })).Passed.ShouldBeFalse();

    [Fact]
    public void Returned_at_fails_a_hook_that_fires_on_every_update() =>
        Grade("hook_returned_at", Proposing("hook_returned_at", document => ReturnedAtHook(document).Remove("condition"))).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("changed(status)")]
    [InlineData("new.status == 'returned'")]
    [InlineData("old.status != 'returned'")]
    public void Returned_at_fails_a_condition_that_does_not_say_the_status_became_returned(string condition) =>
        Grade("hook_returned_at", Proposing("hook_returned_at", document => ReturnedAtHook(document)["condition"] = condition)).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("new.status == 'returned' && changed(status)")]
    [InlineData("new.status == \"returned\" && old.status != \"returned\"")]
    public void Returned_at_passes_either_way_of_saying_the_status_became_returned(string condition) =>
        Grade("hook_returned_at", Proposing("hook_returned_at", document => ReturnedAtHook(document)["condition"] = condition)).Passed.ShouldBeTrue();

    [Fact]
    public void Returned_at_fails_a_hook_that_sets_another_value() =>
        Grade("hook_returned_at", Proposing("hook_returned_at", document =>
            ReturnedAtHook(document)["action"]!["mutate"]!["returned_at"]!["$cel"] = "new.due_at")).Passed.ShouldBeFalse();

    [Fact]
    public void Returned_at_fails_the_right_hook_in_the_wrong_slot() =>
        Grade("hook_returned_at", Proposing("hook_returned_at", document =>
        {
            var hooks = document["entities"]!["rentals"]!["hooks"]!.AsObject();
            var slot = hooks["beforeUpdate"]!;
            hooks.Remove("beforeUpdate");
            hooks["beforeCreate"] = slot;
        })).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("beforeCreate")]
    [InlineData("beforeUpdate")]
    public void Negative_price_fails_a_reject_in_one_slot_only(string removed) =>
        Grade("reject_negative_price", Proposing("reject_negative_price", document => PartsHooks(document).Remove(removed))).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("new.unit_price >= 0.0")]
    [InlineData("new.unit_price > 100.0")]
    [InlineData("new.unit_price < 0.5")]
    [InlineData("new.name == ''")]
    [InlineData("old.unit_price < 0.0")]
    [InlineData("unit_price < 0.0")]
    [InlineData("!(old.unit_price >= 0.0)")]
    public void Negative_price_fails_a_condition_that_is_not_a_negative_price(string condition) =>
        Grade("reject_negative_price", Proposing("reject_negative_price", document => EachPriceHook(document, hook => hook["condition"] = condition)))
            .Passed.ShouldBeFalse();

    [Theory]
    [InlineData("new.unit_price < 0")]
    [InlineData("0.0 > new.unit_price")]
    [InlineData("new.unit_price < 0.00")]
    [InlineData("!(new.unit_price >= 0.0)")]
    public void Negative_price_passes_every_way_of_writing_a_negative_price(string condition) =>
        Grade("reject_negative_price", Proposing("reject_negative_price", document => EachPriceHook(document, hook => hook["condition"] = condition)))
            .Passed.ShouldBeTrue();

    [Fact]
    public void Negative_price_fails_a_hook_that_mutates_instead_of_rejecting() =>
        Grade("reject_negative_price", Proposing("reject_negative_price", document => EachPriceHook(document, hook =>
            hook["action"] = new JsonObject { ["mutate"] = new JsonObject { ["unit_price"] = 0 } }))).Passed.ShouldBeFalse();

    [Fact]
    public void Negative_price_fails_the_right_hooks_beside_a_validation_facet() =>
        Grade("reject_negative_price", Proposing("reject_negative_price", document =>
            document.Fields("parts")["unit_price"]!["validation"] = "value >= 0")).Passed.ShouldBeFalse();

    [Fact]
    public void Rentals_count_fails_the_right_rollup_beside_a_counter_hook() =>
        Grade("rollup_rentals_count", Proposing("rollup_rentals_count", document =>
            document["entities"]!["rentals"]!["hooks"]!["beforeCreate"] = new JsonArray(Reject("false")))).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("op", "sum")]
    [InlineData("from", "bikes")]
    public void Rentals_count_fails_another_aggregate(string member, string value) =>
        Grade("rollup_rentals_count", Proposing("rollup_rentals_count", document => document.Fields("customers")["rentals_count"]!["rollup"]![member] = value))
            .Passed.ShouldBeFalse();

    [Fact]
    public void Rentals_count_fails_the_right_rollup_on_another_entity() =>
        Grade("rollup_rentals_count", Proposing("rollup_rentals_count", document =>
        {
            var field = document.Fields("customers")["rentals_count"]!.DeepClone();
            document.Fields("customers").Remove("rentals_count");
            document.Fields("bikes")["rentals_count"] = field;
        })).Passed.ShouldBeFalse();

    [Fact]
    public void Unique_part_fails_the_right_index_beside_a_part_unique_across_the_table() =>
        Grade("unique_part_per_order", Proposing("unique_part_per_order", document =>
            document.Fields("order_lines")["part_id"]!["unique"] = true)).Passed.ShouldBeFalse();

    [Fact]
    public void Unique_part_fails_the_right_index_that_is_not_unique() =>
        Grade("unique_part_per_order", Proposing("unique_part_per_order", document => NewIndex(document).Remove("unique"))).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("order_id")]
    [InlineData("order_id", "part_id", "kind")]
    public void Unique_part_fails_a_unique_index_over_other_fields(params string[] fields) =>
        Grade("unique_part_per_order", Proposing("unique_part_per_order", document =>
            NewIndex(document)["fields"] = new JsonArray([.. fields.Select(field => (JsonNode)field)]))).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("list", "'admin' in @user.roles || assigned_user_id == @user.id")]
    [InlineData("get", "'admin' in @user.roles || assigned_user_id == @user.id")]
    [InlineData("list", "'admin' in @user.roles || 'manager' in @user.roles")]
    [InlineData("get", "'manager' in @user.roles || assigned_user_id == @user.id")]
    [InlineData("get", "'admin' in @user.roles || 'manager' in @user.roles || 'authenticated' in @user.roles || assigned_user_id == @user.id")]
    public void Own_orders_fails_a_read_rule_that_drops_a_grant_or_keeps_everyone(string operation, string rule) =>
        Grade("own_orders_only", Proposing("own_orders_only", document => document["entities"]!["service_orders"]!["rules"]![operation] = rule))
            .Passed.ShouldBeFalse();

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    public void Own_orders_fails_a_change_to_only_one_of_the_read_rules(string kept) =>
        Grade("own_orders_only", Proposing("own_orders_only", document =>
            document["entities"]!["service_orders"]!["rules"]![kept] = "'authenticated' in @user.roles")).Passed.ShouldBeFalse();

    [Fact]
    public void Own_orders_fails_the_right_read_rules_beside_a_changed_update_rule() =>
        Grade("own_orders_only", Proposing("own_orders_only", document =>
            document["entities"]!["service_orders"]!["rules"]!["update"] = "'admin' in @user.roles")).Passed.ShouldBeFalse();

    [Fact]
    public void Own_orders_passes_double_quotes_and_the_operands_either_way_round() =>
        Grade("own_orders_only", Proposing("own_orders_only", document =>
        {
            const string rule = "\"admin\" in @user.roles || \"manager\" in @user.roles || @user.id == assigned_user_id";
            document["entities"]!["service_orders"]!["rules"]!["list"] = rule;
            document["entities"]!["service_orders"]!["rules"]!["get"] = rule;
        })).Passed.ShouldBeTrue();

    [Fact]
    public void Function_action_fails_a_proposed_function_hook()
    {
        using var hook = JsonDocument.Parse(SkillCaseAnswers.FunctionHook);
        var proposed = DescriptorDiff.Patched(Original, hook.RootElement);

        Grade("function_action_refused", Turn(proposed, FunctionAnswer, calls: [Read("get_capabilities"), Propose(Valid(), round: 2)]))
            .Passed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("webhook", "endpoint", "invoicing")]
    [InlineData("http.call", "url", "https://erp.example.com/invoices")]
    public void Function_action_fails_a_substitute_hook(string type, string member, string value)
    {
        var proposed = Edited(document => document["entities"]!["service_orders"]!["hooks"]!["afterUpdate"]!.AsArray().Add(
            new JsonObject { ["action"] = new JsonObject { ["type"] = type, [member] = value } }));

        Grade("function_action_refused", Turn(proposed, FunctionAnswer, calls: [Read("get_capabilities"), Propose(Valid(), round: 2)]))
            .Passed.ShouldBeFalse();
    }

    [Fact]
    public void Function_action_fails_an_answer_that_never_read_the_capabilities() =>
        Grade("function_action_refused", Turn(answer: FunctionAnswer)).Passed.ShouldBeFalse();

    [Fact]
    public void Function_action_fails_an_empty_answer() =>
        Grade("function_action_refused", Turn(answer: "  ", calls: Read("get_capabilities"))).Passed.ShouldBeFalse();

    [Fact]
    public void Http_question_fails_an_answer_that_never_read_the_capabilities() =>
        Grade("can_alvo_call_http", Turn(answer: HttpAnswer)).Passed.ShouldBeFalse();

    [Fact]
    public void Http_question_fails_an_empty_answer() =>
        Grade("can_alvo_call_http", Turn(answer: "  ", calls: Read("get_capabilities"))).Passed.ShouldBeFalse();

    [Fact]
    public void Http_question_fails_a_right_answer_that_also_proposed() =>
        Grade("can_alvo_call_http", Turn(Edited(document => document.Fields("parts")["erp_synced"] = new JsonObject { ["type"] = "boolean" }),
            HttpAnswer, calls: [Read("get_capabilities"), Propose(Valid(), round: 2)])).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("Yes, it can: add an after-hook http.call.")]
    [InlineData("Áno, dokáže to cez http.call.")]
    [InlineData("It can call it from an after-hook.")]
    [InlineData("**Yes**, it can: add an after-hook http.call.")]
    [InlineData("Yes it can, with an after-hook.")]
    [InlineData("Yes! Add an after-hook http.call.")]
    [InlineData("Yes — via a hook.")]
    [InlineData("Alvo vie zavolať HTTP API cez after-hook.")]
    [InlineData("Alvo dokáže zavolať HTTP API cez after-hook.")]
    [InlineData("> http.call is refused.\n\n_Yes_, a webhook does it.")]
    public void Http_question_fails_an_answer_that_claims_the_capability(string answer) =>
        Grade("can_alvo_call_http", Turn(answer: answer, calls: Read("get_capabilities"))).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("Nie, táto verzia http.call nevolá; webhook áno, ale nepodpísaný.")]
    [InlineData("**No.** This build refuses an http.call action.")]
    [InlineData("It can't: this build refuses an http.call action.")]
    public void Http_question_passes_a_negative_answer(string answer) =>
        Grade("can_alvo_call_http", Turn(answer: answer, calls: Read("get_capabilities"))).Passed.ShouldBeTrue();

    private static TurnRecord RightAnswer(string name) => name switch
    {
        "function_action_refused" => Turn(answer: FunctionAnswer, calls: [Loads(Capabilities, 1), Read("get_capabilities", 1)]),
        "can_alvo_call_http" => Turn(answer: HttpAnswer, calls: [Loads(Capabilities, 1), Read("get_capabilities", 1)]),
        _ => Proposing(name),
    };

    /// <summary>The case's right answer, with <paramref name="change"/> applied on top — the one wrong thing, when given.</summary>
    private static TurnRecord Proposing(string name, Action<JsonObject>? change = null)
    {
        var document = JsonNode.Parse(Answer(name))!.AsObject();
        change?.Invoke(document);
        return Turn(document.ToJsonString(), Proposed, calls: [Loads(_skillOf[name], 1), Read("get_descriptor", 1), Propose(Valid(), round: 2)]);
    }

    /// <summary>The case's right-answer patch applied to the fixture.</summary>
    private static string Answer(string name)
    {
        using var operations = JsonDocument.Parse(SkillCaseAnswers.RightAnswers[name]);
        return DescriptorDiff.Patched(Original, operations.RootElement);
    }

    private static JsonObject ReturnedAtHook(JsonObject document) =>
        document["entities"]!["rentals"]!["hooks"]!["beforeUpdate"]![0]!.AsObject();

    private static JsonObject PartsHooks(JsonObject document) => document["entities"]!["parts"]!["hooks"]!.AsObject();

    private static void EachPriceHook(JsonObject document, Action<JsonObject> change)
    {
        foreach (var slot in PartsHooks(document))
        {
            change(slot.Value![0]!.AsObject());
        }
    }

    private static JsonObject NewIndex(JsonObject document) => document["entities"]!["order_lines"]!["indexes"]!.AsArray()[^1]!.AsObject();

    private static JsonObject Reject(string condition) => new()
    {
        ["condition"] = condition,
        ["action"] = new JsonObject { ["reject"] = "No." },
    };

    private static Verdict Grade(string name, TurnRecord turn) => Case(name).Grade(turn);

    private static EvalCase Case(string name) => EvalCases.All.Single(candidate => candidate.Name == name);
}
