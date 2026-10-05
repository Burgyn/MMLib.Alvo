using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;
using NSubstitute;
using System.Security.Claims;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The candidate descriptor an expression input is checked against: built on a clone, never on the working copy.
/// </summary>
public class ExpressionSlotsTests
{
    private const string Working = """{"entities":{"orders":{"fields":{},"rules":{"get":"true"}},"bare":{"fields":{}},"a/b":{"fields":{}}}}""";

    [Fact]
    public void The_candidate_lands_at_the_returned_path()
    {
        var (json, path) = ExpressionSlots.ForRule(Working, "orders", "list", "x == 1")!.Value;

        path.ShouldBe("/entities/orders/rules/list");
        JsonNode.Parse(json)!["entities"]!["orders"]!["rules"]!["list"]!.GetValue<string>().ShouldBe("x == 1");
    }

    [Fact]
    public void An_entity_without_rules_gets_the_rules_object_on_the_clone()
    {
        var (json, path) = ExpressionSlots.ForRule(Working, "bare", "create", "false")!.Value;

        path.ShouldBe("/entities/bare/rules/create");
        JsonNode.Parse(json)!["entities"]!["bare"]!["rules"]!["create"]!.GetValue<string>().ShouldBe("false");
    }

    [Fact]
    public void Two_candidates_from_the_same_working_text_are_independent()
    {
        var (first, _) = ExpressionSlots.ForRule(Working, "bare", "create", "first")!.Value;
        var (second, _) = ExpressionSlots.ForRule(Working, "bare", "create", "second")!.Value;

        first.ShouldContain("first");
        first.ShouldNotContain("second");
        second.ShouldNotContain("first");
    }

    [Fact]
    public void A_candidate_for_one_slot_does_not_carry_another_drafts_slot()
    {
        var (json, _) = ExpressionSlots.ForRule(Working, "bare", "create", "false")!.Value;
        var (other, _) = ExpressionSlots.ForRule(Working, "orders", "list", "true")!.Value;

        json.ShouldNotContain("\"list\"");
        other.ShouldNotContain("\"create\"");
    }

    [Fact]
    public void An_existing_rule_is_replaced_by_the_candidate_and_the_others_stay()
    {
        var (json, _) = ExpressionSlots.ForRule(Working, "orders", "get", "false")!.Value;

        var rules = JsonNode.Parse(json)!["entities"]!["orders"]!["rules"]!;
        rules["get"]!.GetValue<string>().ShouldBe("false");
    }

    [Fact]
    public void A_slash_in_the_entity_name_is_escaped_in_the_pointer()
    {
        var (json, path) = ExpressionSlots.ForRule(Working, "a/b", "list", "true")!.Value;

        path.ShouldBe("/entities/a~1b/rules/list");
        JsonNode.Parse(json)!["entities"]!["a/b"]!["rules"]!["list"].ShouldNotBeNull();
    }

    [Fact]
    public void A_tilde_is_escaped_before_the_slash()
        => ExpressionSlots.Pointer("entities", "a~b/c").ShouldBe("/entities/a~0b~1c");

    [Fact]
    public void An_unknown_entity_has_nothing_to_check()
        => ExpressionSlots.ForRule(Working, "missing", "list", "true").ShouldBeNull();

    [Fact]
    public void Text_that_is_not_a_descriptor_has_nothing_to_check()
        => ExpressionSlots.ForRule("not json", "orders", "list", "true").ShouldBeNull();

    private const string WithHooks = """{"entities":{"orders":{"fields":{"total":{"type":"decimal"}},"hooks":{"beforeCreate":[{"action":{"reject":"no"}}]}},"bare":{"fields":{}},"a/b":{"fields":{}}}}""";

    private static readonly Dictionary<string, FieldSchema> _total = new(StringComparer.Ordinal)
    {
        ["total"] = new() { Name = "total", Type = FieldType.Decimal },
    };

    [Fact]
    public void A_mutate_field_with_a_slash_is_escaped_in_the_hook_pointer()
    {
        var hook = new HookBuilder { Kind = HookBuilder.Mutate, MutateRows = { new MutateRow("a/b", MutateMode.Expression, "1") } };

        var (_, path) = ExpressionSlots.ForHook(
            WithHooks, "orders", "beforeUpdate", null, hook.CandidateHook(null), "action", "mutate", "a/b")!.Value;

        path.ShouldBe("/entities/orders/hooks/beforeUpdate/0/action/mutate/a~1b");
    }

    /// <summary>
    /// The value is checked in a hook without its condition (slice B design D4): the before-hook compiler stops at a
    /// condition that does not compile and never reaches the value. And the check must key the mutate exactly as Add
    /// stages it: when the two disagreed (Add kept <c>total </c> as typed, the check trimmed it), the check found no such
    /// key and stayed silent while Apply refused the hook. Add stages the trimmed name, so the candidate carries it too.
    /// </summary>
    [Fact]
    public void The_form_s_mutate_value_is_placed_without_the_condition_under_the_key_add_stages()
    {
        var hook = new HookBuilder
        {
            Kind = HookBuilder.Mutate,
            Condition = "new.total ==",
            Fields = _total,
            MutateRows = { new MutateRow("total ", MutateMode.Expression, "1") },
        };

        var (json, path) = ExpressionSlots.ForHook(
            WithHooks, "orders", hook.Point, null, hook.CandidateHook(null), "action", "mutate", hook.MutateRows[0].Field.Trim())!.Value;

        hook.Build(out var refusal).ShouldNotBeNull(refusal)["mutate"]!.AsObject().Select(pair => pair.Key)
            .ShouldBe(["total"], "what Add would stage");
        path.ShouldEndWith("/action/mutate/total");
        var placed = JsonNode.Parse(json)!["entities"]!["orders"]!["hooks"]![hook.Point]!.AsArray()[^1]!;
        placed["action"]!["mutate"]!["total"].ShouldNotBeNull();
        placed["condition"].ShouldBeNull("a broken condition in the candidate would hide every problem in the value");
    }

    [Fact]
    public void A_computed_expression_lands_in_the_new_field_with_the_typed_source()
    {
        var facets = new JsonObject { ["type"] = "decimal", ["computed"] = "stale" };
        var (json, path) = ExpressionSlots.ForComputed(WithHooks, "orders", null, "double_total", facets, "total * 2")!.Value;

        path.ShouldBe("/entities/orders/fields/double_total/computed");
        var field = JsonNode.Parse(json)!["entities"]!["orders"]!["fields"]!["double_total"]!;
        field["computed"]!.GetValue<string>().ShouldBe("total * 2");
        field["type"]!.GetValue<string>().ShouldBe("decimal");
        facets["computed"]!.GetValue<string>().ShouldBe("stale");
    }

    [Fact]
    public void A_computed_field_on_an_entity_with_slash_is_escaped_and_independent_of_the_other_candidate()
    {
        var facets = new JsonObject { ["type"] = "decimal" };
        var (first, path) = ExpressionSlots.ForComputed(WithHooks, "a/b", null, "f~g", facets, "first")!.Value;
        var (second, _) = ExpressionSlots.ForComputed(WithHooks, "a/b", null, "f~g", facets, "second")!.Value;

        path.ShouldBe("/entities/a~1b/fields/f~0g/computed");
        first.ShouldNotContain("second");
        second.ShouldNotContain("first");
    }

    private const string WithComputed = """{"entities":{"orders":{"fields":{"total":{"type":"decimal","computed":"price * 2"},"price":{"type":"decimal"}}}}}""";

    /// <summary>
    /// Save writes an edited field over its old name and then renames it, which removes the old key: a candidate that
    /// only added the new name kept <c>total</c>, so <c>total * 2</c> under <c>subtotal</c> read green and Apply refused.
    /// </summary>
    [Fact]
    public void A_renamed_computed_field_leaves_its_old_name_behind_as_save_does()
    {
        var facets = new JsonObject { ["type"] = "decimal" };
        var (json, path) = ExpressionSlots.ForComputed(WithComputed, "orders", "total", "subtotal", facets, "total * 2")!.Value;

        path.ShouldBe("/entities/orders/fields/subtotal/computed");
        var fields = JsonNode.Parse(json)!["entities"]!["orders"]!["fields"]!.AsObject();
        fields.ContainsKey("total").ShouldBeFalse("the rename removed the old key, so the expression names nothing");
        fields["subtotal"]!["type"]!.GetValue<string>().ShouldBe("decimal");
        facets.ContainsKey("computed").ShouldBeFalse("the form's facets are not modified");
    }

    [Fact]
    public void A_renamed_computed_field_with_a_valid_expression_keeps_the_fields_it_reads()
    {
        var facets = new JsonObject { ["type"] = "decimal" };
        var (json, path) = ExpressionSlots.ForComputed(WithComputed, "orders", "total", "twice", facets, "price * 2")!.Value;

        path.ShouldBe("/entities/orders/fields/twice/computed");
        var fields = JsonNode.Parse(json)!["entities"]!["orders"]!["fields"]!.AsObject();
        fields.ContainsKey("total").ShouldBeFalse();
        fields["twice"]!["computed"]!.GetValue<string>().ShouldBe("price * 2");
        fields.ContainsKey("price").ShouldBeTrue();
    }

    [Fact]
    public void A_computed_field_on_an_unknown_entity_has_nothing_to_check()
        => ExpressionSlots.ForComputed(WithHooks, "missing", null, "f", new JsonObject(), "x").ShouldBeNull();

    [Fact]
    public void The_hook_draft_is_the_built_action_once_the_form_can_build_one()
    {
        var hook = new HookBuilder { Kind = HookBuilder.Reject, RejectMessage = "stop" };

        hook.Draft().ToJsonString().ShouldBe("""{"reject":"stop"}""");
    }

    [Theory]
    [InlineData(HookBuilder.Reject)]
    [InlineData(HookBuilder.Mutate)]
    public void The_hook_draft_of_an_unfinished_form_is_the_same_kind_with_stand_ins(string kind)
    {
        var hook = new HookBuilder { Kind = kind, MutateRows = { new MutateRow("total", MutateMode.Expression, string.Empty) } };

        hook.Draft().ContainsKey(kind).ShouldBeTrue();
        if (kind == HookBuilder.Mutate)
        {
            hook.Draft()["mutate"]!["total"].ShouldNotBeNull();
        }
    }

    [Fact]
    public void An_unfinished_webhook_and_email_draft_carry_their_required_facets()
    {
        new HookBuilder { Kind = HookBuilder.Webhook }.Draft()["endpoint"].ShouldNotBeNull();
        new HookBuilder { Kind = HookBuilder.Email }.Draft()["template"].ShouldNotBeNull();
    }

    [Fact]
    public async Task A_check_round_leaves_the_working_copy_byte_identical()
    {
        var copy = new WorkingCopy();
        copy.Take(WithHooks, revision: 1);
        var before = copy.Json;
        var management = Substitute.For<IAlvoManagement>();
        management.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns([new ManagementProject("default", 1, "applied")]);
        management.CheckExpressionAsync(Arg.Any<string>(), Arg.Any<ManagementExpressionCheck>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementExpressionVerdict([]));
        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal())));
        using var gateway = new ManagementGateway(
            management, people: null, Substitute.For<IAlvoAdminCallerResolver>(), authentication,
            Substitute.For<IAlvoContextAccessor>());
        var check = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        var hook = new HookBuilder { Kind = HookBuilder.Mutate, MutateRows = { new MutateRow("total", MutateMode.Expression, string.Empty) } };

        await check.SubmitAsync("hook-condition", "new.total > 1", async (source, ct) =>
        {
            var slot = ExpressionSlots.ForHook(
                copy.Json, "orders", "beforeCreate", null, HookPatch.Apply(null, source, hook.Draft()), "condition")!.Value;
            return await gateway.CheckExpressionAsync(slot.Json, slot.Path, source, ct);
        });
        await check.SubmitAsync("hook-condition", string.Empty, (_, _) => Task.FromResult<ManagementExpressionVerdict?>(null));

        copy.Json.ShouldBe(before);
        copy.IsDirty.ShouldBeFalse();
        copy.PendingCount.ShouldBe(0);
        await management.Received().CheckExpressionAsync(
            Arg.Any<string>(), Arg.Any<ManagementExpressionCheck>(), Arg.Any<CancellationToken>());
    }
}
