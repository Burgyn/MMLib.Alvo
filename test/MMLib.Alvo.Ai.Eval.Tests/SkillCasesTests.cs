using System.Text.Json.Nodes;

using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>
/// The seven skill cases (spec §7.5, D36): each passes its right answer, and fails that answer with exactly one thing
/// wrong. Every right answer also passes the whole grading, loading the skill its proposal needs (D31).
/// </summary>
/// <remarks>The right answers' patches are proven valid against the real validator in <c>SkillCaseAnswerTests</c> (Host.Tests).</remarks>
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
    private const string OwnOrdersRule = "'admin' in @user.roles || 'manager' in @user.roles || assigned_user_id == @user.id";

    public static TheoryData<string> RightAnswers() =>
        ["hook_returned_at", "reject_negative_price", "rollup_rentals_count", "unique_part_per_order", "own_orders_only", "function_action_refused", "can_alvo_call_http"];

    [Theory]
    [MemberData(nameof(RightAnswers))]
    public void A_right_answer_passes_its_case(string name) =>
        Grade(name, RightAnswer(name)).Passed.ShouldBeTrue(Grade(name, RightAnswer(name)).Why);

    [Theory]
    [MemberData(nameof(RightAnswers))]
    public void A_right_answer_passes_the_whole_grading_with_the_skill_it_needs(string name)
    {
        var verdict = EvalRunner.Graded(Case(name), ReplyLanguage.English, RightAnswer(name));

        verdict.Passed.ShouldBeTrue(verdict.Why);
    }

    [Fact]
    public void Returned_at_fails_a_hook_that_fires_on_every_update()
    {
        var hook = ReturnedAtHook();
        hook.Remove("condition");

        Grade("hook_returned_at", Proposing(WithHook("rentals", "beforeUpdate", hook), Hooks)).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Returned_at_fails_a_hook_that_sets_another_value() =>
        Grade("hook_returned_at", Proposing(WithHook("rentals", "beforeUpdate", ReturnedAtHook("new.due_at")), Hooks)).Passed.ShouldBeFalse();

    [Fact]
    public void Negative_price_fails_the_right_hooks_beside_a_validation_facet() =>
        Grade("reject_negative_price", Proposing(Edited(document =>
        {
            WithRejectHooks(document);
            document.Fields("parts")["unit_price"]!["validation"] = "value >= 0";
        }), Hooks)).Passed.ShouldBeFalse();

    [Fact]
    public void Rentals_count_fails_the_right_rollup_beside_a_counter_hook() =>
        Grade("rollup_rentals_count", Proposing(Edited(document =>
        {
            WithRentalsCount(document);
            Hook(document, "rentals", "beforeCreate", Reject("false"));
        }), Computed)).Passed.ShouldBeFalse();

    [Fact]
    public void Unique_part_fails_the_right_index_beside_a_part_unique_across_the_table() =>
        Grade("unique_part_per_order", Proposing(Edited(document =>
        {
            WithUniquePartIndex(document);
            document.Fields("order_lines")["part_id"]!["unique"] = true;
        }), Indexes)).Passed.ShouldBeFalse();

    [Fact]
    public void Own_orders_fails_a_get_rule_that_drops_the_managers() =>
        Grade("own_orders_only", Proposing(Edited(document =>
        {
            WithReadRules(document, OwnOrdersRule);
            document["entities"]!["service_orders"]!["rules"]!["get"] = "'admin' in @user.roles || assigned_user_id == @user.id";
        }), Rules)).Passed.ShouldBeFalse();

    [Fact]
    public void Function_action_fails_a_proposed_function_hook()
    {
        var proposed = Edited(document => Hook(document, "service_orders", "afterUpdate", new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "function", ["name"] = "invoice" },
        }));

        Grade("function_action_refused", Turn(proposed, FunctionAnswer, calls: [Read("get_capabilities"), Propose(Valid(), round: 2)]))
            .Passed.ShouldBeFalse();
    }

    [Fact]
    public void Function_action_fails_an_answer_that_never_read_the_capabilities() =>
        Grade("function_action_refused", Turn(answer: FunctionAnswer)).Passed.ShouldBeFalse();

    [Fact]
    public void Http_question_fails_an_answer_that_never_read_the_capabilities() =>
        Grade("can_alvo_call_http", Turn(answer: HttpAnswer)).Passed.ShouldBeFalse();

    [Theory]
    [InlineData("Yes, it can: add an after-hook http.call.")]
    [InlineData("Áno, dokáže to cez http.call.")]
    [InlineData("It can call it from an after-hook.")]
    public void Http_question_fails_an_answer_that_claims_the_capability(string answer) =>
        Grade("can_alvo_call_http", Turn(answer: answer, calls: Read("get_capabilities"))).Passed.ShouldBeFalse();

    private static TurnRecord RightAnswer(string name) => name switch
    {
        "hook_returned_at" => Proposing(WithHook("rentals", "beforeUpdate", ReturnedAtHook()), Hooks),
        "reject_negative_price" => Proposing(Edited(WithRejectHooks), Hooks),
        "rollup_rentals_count" => Proposing(Edited(WithRentalsCount), Computed),
        "unique_part_per_order" => Proposing(Edited(WithUniquePartIndex), Indexes),
        "own_orders_only" => Proposing(Edited(document => WithReadRules(document, OwnOrdersRule)), Rules),
        "function_action_refused" => Turn(answer: FunctionAnswer, calls: [Loads(Capabilities, 1), Read("get_capabilities", 1)]),
        "can_alvo_call_http" => Turn(answer: HttpAnswer, calls: [Loads(Capabilities, 1), Read("get_capabilities", 1)]),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no right answer"),
    };

    private static TurnRecord Proposing(string proposed, string skill) =>
        Turn(proposed, Proposed, calls: [Loads(skill, 1), Read("get_descriptor", 1), Propose(Valid(), round: 2)]);

    private static JsonObject ReturnedAtHook(string value = "now()") => new()
    {
        ["condition"] = "new.status == 'returned' && old.status != 'returned'",
        ["action"] = new JsonObject { ["mutate"] = new JsonObject { ["returned_at"] = new JsonObject { ["$cel"] = value } } },
    };

    private static JsonObject Reject(string condition) => new()
    {
        ["condition"] = condition,
        ["action"] = new JsonObject { ["reject"] = "The selling price of a part cannot be negative." },
    };

    private static void WithRejectHooks(JsonObject document)
    {
        Hook(document, "parts", "beforeCreate", Reject("new.unit_price < 0.0"));
        Hook(document, "parts", "beforeUpdate", Reject("new.unit_price < 0.0"));
    }

    private static void WithRentalsCount(JsonObject document) =>
        document.Fields("customers")["rentals_count"] = new JsonObject
        {
            ["type"] = "integer",
            ["rollup"] = new JsonObject { ["from"] = "rentals", ["op"] = "count" },
        };

    private static void WithUniquePartIndex(JsonObject document) =>
        document["entities"]!["order_lines"]!["indexes"]!.AsArray().Add(
            new JsonObject { ["fields"] = new JsonArray("part_id", "order_id"), ["unique"] = true });

    private static void WithReadRules(JsonObject document, string rule)
    {
        document["entities"]!["service_orders"]!["rules"]!["list"] = rule;
        document["entities"]!["service_orders"]!["rules"]!["get"] = rule;
    }

    private static string WithHook(string entity, string slot, JsonObject hook) => Edited(document => Hook(document, entity, slot, hook));

    /// <summary>Appends <paramref name="hook"/> to one slot, creating the slot, and <c>hooks</c> itself, only when missing (B4).</summary>
    private static void Hook(JsonObject document, string entity, string slot, JsonObject hook)
    {
        var owner = document["entities"]![entity]!.AsObject();
        if (owner["hooks"] is not JsonObject hooks)
        {
            hooks = [];
            owner["hooks"] = hooks;
        }

        if (hooks[slot] is not JsonArray list)
        {
            list = [];
            hooks[slot] = list;
        }

        list.Add(hook);
    }

    private static Verdict Grade(string name, TurnRecord turn) => Case(name).Grade(turn);

    private static EvalCase Case(string name) => EvalCases.All.Single(candidate => candidate.Name == name);
}
