using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Management;

using System.Text.Json;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// Each proposing skill case of the eval (spec §7.5) has a right answer the real validator accepts on
/// <c>bike-workshop</c>, and the <c>function</c> case's wrong answer is refused as unhonoured.
/// </summary>
/// <remarks>
/// A case whose right answer the validator refused would grade every model a failure. The patches are the right answers
/// <c>SkillCasesTests</c> grades (in <c>MMLib.Alvo.Ai.Eval.Tests</c>), written against the real descriptor.
/// </remarks>
public sealed class SkillCaseAnswerTests
{
    private const string ReadRule = "'admin' in @user.roles || 'manager' in @user.roles || assigned_user_id == @user.id";
    private const string RejectPrice =
        """{"condition": "new.unit_price < 0.0", "action": {"reject": "The selling price of a part cannot be negative."}}""";

    private const string FunctionHook = """
        [{"op": "add", "path": "/entities/service_orders/hooks/afterUpdate/-",
          "value": {"condition": "new.status == 'ready'", "action": {"type": "function", "name": "invoice"}}}]
        """;

    private static readonly Dictionary<string, string> _rightAnswers = new(StringComparer.Ordinal)
    {
        ["hook_returned_at"] = """
            [{"op": "add", "path": "/entities/rentals/hooks/beforeUpdate",
              "value": [{"condition": "new.status == 'returned' && old.status != 'returned'",
                         "action": {"mutate": {"returned_at": {"$cel": "now()"}}}}]}]
            """,
        ["reject_negative_price"] = $$$"""
            [{"op": "add", "path": "/entities/parts/hooks", "value": {"beforeCreate": [{{{RejectPrice}}}], "beforeUpdate": [{{{RejectPrice}}}]}}]
            """,
        ["rollup_rentals_count"] = """
            [{"op": "add", "path": "/entities/customers/fields/rentals_count",
              "value": {"type": "integer", "rollup": {"from": "rentals", "op": "count"}}}]
            """,
        ["unique_part_per_order"] = """
            [{"op": "add", "path": "/entities/order_lines/indexes/-", "value": {"fields": ["part_id", "order_id"], "unique": true}}]
            """,
        ["own_orders_only"] = $$"""
            [{"op": "replace", "path": "/entities/service_orders/rules/list", "value": "{{ReadRule}}"},
             {"op": "replace", "path": "/entities/service_orders/rules/get", "value": "{{ReadRule}}"}]
            """,
    };

    public static TheoryData<string> Cases() => [.. _rightAnswers.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_skill_cases_right_answer_passes_the_real_dry_run(string name)
    {
        var attempt = await AttemptAsync(_rightAnswers[name]);

        attempt.Valid.ShouldBeTrue($"{name}: {string.Join(" | ", attempt.Refusals)}");
    }

    [Fact]
    public async Task The_function_cases_wrong_answer_is_refused_as_unhonoured()
    {
        var attempt = await AttemptAsync(FunctionHook);

        attempt.Valid.ShouldBeFalse();
        attempt.Refusals.ShouldContain(
            refusal => refusal.Contains(UnhonouredFeatures.UnhonouredAction("function").Consequence, StringComparison.Ordinal),
            string.Join(" | ", attempt.Refusals));
    }

    private static async Task<Ai.Internal.DraftAttempt> AttemptAsync(string operations)
    {
        await using var world = await AlvoHostWorld.StartAsync(InstructionExampleOutcomeTests.BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = InstructionExampleOutcomeTests.Administrator();

        using var document = JsonDocument.Parse(operations);
        return await InstructionExampleOutcomeTests.AttemptAsync(management, document.RootElement.Clone());
    }
}
