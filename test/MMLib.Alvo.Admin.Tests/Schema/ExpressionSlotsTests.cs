using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
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

    private static JsonObject Reject() => new() { ["reject"] = "stop" };

    private static JsonObject Mutate(string field) => new() { ["mutate"] = new JsonObject { [field] = new JsonObject { ["$cel"] = "old" } } };

    [Fact]
    public void A_hook_condition_lands_after_the_hooks_already_at_that_point()
    {
        var (json, path) = ExpressionSlots.ForHookCondition(WithHooks, "orders", "beforeCreate", Reject(), "new.total > 1")!.Value;

        path.ShouldBe("/entities/orders/hooks/beforeCreate/1/condition");
        var list = JsonNode.Parse(json)!["entities"]!["orders"]!["hooks"]!["beforeCreate"]!.AsArray();
        list.Count.ShouldBe(2);
        list[1]!["condition"]!.GetValue<string>().ShouldBe("new.total > 1");
        list[1]!["action"]!["reject"]!.GetValue<string>().ShouldBe("stop");
    }

    [Fact]
    public void A_hook_at_a_point_with_none_is_the_first_and_creates_the_hooks_object()
    {
        var (json, path) = ExpressionSlots.ForHookCondition(WithHooks, "bare", "afterUpdate", Reject(), "true")!.Value;

        path.ShouldBe("/entities/bare/hooks/afterUpdate/0/condition");
        JsonNode.Parse(json)!["entities"]!["bare"]!["hooks"]!["afterUpdate"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void A_hook_candidate_leaves_the_working_text_and_the_action_untouched_and_is_independent()
    {
        var action = Reject();
        var (first, _) = ExpressionSlots.ForHookCondition(WithHooks, "bare", "afterUpdate", action, "first")!.Value;
        var (second, _) = ExpressionSlots.ForHookCondition(WithHooks, "bare", "afterUpdate", action, "second")!.Value;

        first.ShouldNotContain("second");
        second.ShouldNotContain("first");
        action.ToJsonString().ShouldBe("""{"reject":"stop"}""");
        WithHooks.ShouldNotContain("afterUpdate");
    }

    [Fact]
    public void A_slash_in_the_entity_name_is_escaped_in_the_hook_pointer()
    {
        var (_, path) = ExpressionSlots.ForHookCondition(WithHooks, "a/b", "beforeCreate", Reject(), "true")!.Value;

        path.ShouldBe("/entities/a~1b/hooks/beforeCreate/0/condition");
    }

    [Fact]
    public void A_hook_on_an_unknown_entity_or_in_text_that_is_not_a_descriptor_has_nothing_to_check()
    {
        ExpressionSlots.ForHookCondition(WithHooks, "missing", "beforeCreate", Reject(), "true").ShouldBeNull();
        ExpressionSlots.ForHookCondition("not json", "orders", "beforeCreate", Reject(), "true").ShouldBeNull();
    }

    [Fact]
    public void A_mutate_value_lands_under_the_patched_field_with_the_typed_source_and_no_condition()
    {
        var (json, path) = ExpressionSlots.ForMutateValue(
            WithHooks, "orders", "beforeUpdate", Mutate("total"), "total", "now()")!.Value;

        path.ShouldBe("/entities/orders/hooks/beforeUpdate/0/action/mutate/total");
        var hook = JsonNode.Parse(json)!["entities"]!["orders"]!["hooks"]!["beforeUpdate"]![0]!;
        hook["action"]!["mutate"]!["total"]!["$cel"]!.GetValue<string>().ShouldBe("now()");
        hook["condition"].ShouldBeNull(
            "the before-hook compiler stops at a condition that does not compile, so the value is checked without it");
    }

    [Fact]
    public void A_mutate_field_with_a_slash_is_escaped_and_the_action_is_not_modified()
    {
        var action = Mutate("a/b");
        var (_, path) = ExpressionSlots.ForMutateValue(WithHooks, "orders", "beforeUpdate", action, "a/b", "1")!.Value;

        path.ShouldBe("/entities/orders/hooks/beforeUpdate/0/action/mutate/a~1b");
        action["mutate"]!["a/b"]!["$cel"]!.GetValue<string>().ShouldBe("old");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_mutate_value_with_no_field_named_yet_has_nothing_to_check(string field)
        => ExpressionSlots.ForMutateValue(WithHooks, "orders", "beforeUpdate", Mutate("x"), field, "1").ShouldBeNull();

    [Fact]
    public void A_mutate_value_of_an_action_that_is_not_that_mutate_has_nothing_to_check()
    {
        ExpressionSlots.ForMutateValue(WithHooks, "orders", "beforeUpdate", Reject(), "total", "1").ShouldBeNull();
        ExpressionSlots.ForMutateValue(WithHooks, "missing", "beforeUpdate", Mutate("total"), "total", "1").ShouldBeNull();
    }

    [Fact]
    public void A_computed_expression_lands_in_the_new_field_with_the_typed_source()
    {
        var facets = new JsonObject { ["type"] = "decimal", ["computed"] = "stale" };
        var (json, path) = ExpressionSlots.ForComputed(WithHooks, "orders", "double_total", facets, "total * 2")!.Value;

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
        var (first, path) = ExpressionSlots.ForComputed(WithHooks, "a/b", "f~g", facets, "first")!.Value;
        var (second, _) = ExpressionSlots.ForComputed(WithHooks, "a/b", "f~g", facets, "second")!.Value;

        path.ShouldBe("/entities/a~1b/fields/f~0g/computed");
        first.ShouldNotContain("second");
        second.ShouldNotContain("first");
    }

    [Fact]
    public void A_computed_field_on_an_unknown_entity_has_nothing_to_check()
        => ExpressionSlots.ForComputed(WithHooks, "missing", "f", new JsonObject(), "x").ShouldBeNull();

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
        var hook = new HookBuilder { Kind = kind, MutateField = "total" };

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
        var hook = new HookBuilder { Kind = HookBuilder.Mutate, MutateField = "total" };

        await check.SubmitAsync("hook-condition", "new.total > 1", async (source, ct) =>
        {
            var slot = ExpressionSlots.ForHookCondition(copy.Json, "orders", "beforeCreate", hook.Draft(), source)!.Value;
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
