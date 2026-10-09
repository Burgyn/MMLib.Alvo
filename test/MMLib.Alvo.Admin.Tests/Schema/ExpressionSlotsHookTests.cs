using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Where a whole hook is placed for a check: appended when new, at its own position when edited (spec §4.2, D4).</summary>
public class ExpressionSlotsHookTests
{
    private const string Working = """{"entities":{"orders":{"fields":{"total":{"type":"decimal"}},"hooks":{"beforeUpdate":[{"action":{"reject":"first"}},{"action":{"reject":"second"}}]}},"bare":{"fields":{}},"a/b":{"fields":{}}}}""";

    [Fact]
    public void A_new_hook_is_appended_and_the_slot_names_its_position()
    {
        var (json, path) = ExpressionSlots.ForHook(Working, "orders", "beforeUpdate", null, Hook("third"), "action", "reject")!.Value;

        path.ShouldBe("/entities/orders/hooks/beforeUpdate/2/action/reject");
        var list = List(json, "orders", "beforeUpdate");
        list.Count.ShouldBe(3);
        list[2]!["action"]!["reject"]!.GetValue<string>().ShouldBe("third", "the appended entry is the hook passed");
    }

    [Fact]
    public void An_edited_hook_is_placed_where_it_sits()
    {
        var (json, path) = ExpressionSlots.ForHook(Working, "orders", "beforeUpdate", 0, Hook("edited"), "action", "reject")!.Value;

        path.ShouldBe("/entities/orders/hooks/beforeUpdate/0/action/reject");
        var list = List(json, "orders", "beforeUpdate");
        list.Count.ShouldBe(2);
        list[0]!["action"]!["reject"]!.GetValue<string>().ShouldBe("edited");
        list[1]!["action"]!["reject"]!.GetValue<string>().ShouldBe("second");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void A_position_nothing_is_at_has_nothing_to_check(int position)
        => ExpressionSlots.ForHook(Working, "orders", "beforeUpdate", position, Hook("x"), "action", "reject").ShouldBeNull();

    [Theory]
    [InlineData("missing")]
    public void An_entity_the_copy_does_not_declare_has_nothing_to_check(string entity)
        => ExpressionSlots.ForHook(Working, entity, "beforeUpdate", null, Hook("x"), "action", "reject").ShouldBeNull();

    [Fact]
    public void Text_that_is_not_a_descriptor_has_nothing_to_check()
        => ExpressionSlots.ForHook("not json", "orders", "beforeUpdate", null, Hook("x"), "condition").ShouldBeNull();

    [Fact]
    public void A_point_the_entity_has_no_list_for_is_created_on_the_clone_only()
    {
        var (json, path) = ExpressionSlots.ForHook(Working, "bare", "afterCreate", null, Hook("x"), "action", "reject")!.Value;

        path.ShouldBe("/entities/bare/hooks/afterCreate/0/action/reject");
        List(json, "bare", "afterCreate").Count.ShouldBe(1);
        JsonNode.Parse(Working)!["entities"]!["bare"]!["hooks"].ShouldBeNull();
    }

    [Fact]
    public void A_name_with_a_slash_is_escaped_in_the_pointer()
        => ExpressionSlots.ForHook(Working, "a/b", "beforeCreate", null, Hook("x"), "action", "mutate", "c~d")!.Value.Path
            .ShouldBe("/entities/a~1b/hooks/beforeCreate/0/action/mutate/c~0d");

    [Fact]
    public void The_hook_argument_is_not_attached_to_the_clone()
    {
        var hook = Hook("x");

        ExpressionSlots.ForHook(Working, "orders", "beforeUpdate", null, hook, "action", "reject");

        hook.Parent.ShouldBeNull();
    }

    private static JsonObject Hook(string message) => new() { ["action"] = new JsonObject { ["reject"] = message } };

    private static JsonArray List(string json, string entity, string point)
        => (JsonArray)JsonNode.Parse(json)!["entities"]![entity]!["hooks"]![point]!;
}
