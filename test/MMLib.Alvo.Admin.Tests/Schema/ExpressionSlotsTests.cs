using MMLib.Alvo.Admin.Components.Schema;
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
    public void The_working_json_is_untouched()
    {
        var before = Working;

        _ = ExpressionSlots.ForRule(Working, "bare", "create", "false");

        Working.ShouldBe(before);
        Working.ShouldNotContain("create");
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
}
