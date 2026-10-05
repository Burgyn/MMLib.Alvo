using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What an edit writes onto the hook it opened (spec §5.3, D2): only what the form changed, in the original's order.</summary>
public class HookPatchTests
{
    [Fact]
    public void A_new_hook_is_written_condition_first()
        => Written(null, "new.a == 'x'", Reject("No.")).ShouldBe("""{"condition":"new.a == 'x'","action":{"reject":"No."}}""");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_new_hook_with_no_condition_carries_no_condition_key(string? condition)
        => Written(null, condition, Reject("No.")).ShouldBe("""{"action":{"reject":"No."}}""");

    [Fact]
    public void An_edit_keeps_the_originals_key_order()
        => Written(Parse("""{"action":{"reject":"Old."},"condition":"new.a == 1"}"""), "new.a == 2", Reject("New."))
            .ShouldBe("""{"action":{"reject":"New."},"condition":"new.a == 2"}""");

    [Fact]
    public void Clearing_the_condition_removes_its_key()
        => Written(Parse("""{"condition":"new.a == 1","action":{"reject":"Old."}}"""), string.Empty, Reject("Old."))
            .ShouldBe("""{"action":{"reject":"Old."}}""");

    [Fact]
    public void A_condition_added_to_a_hook_without_one_goes_first()
        => Written(Parse("""{"action":{"reject":"Old."}}"""), "new.a == 1", Reject("Old."))
            .ShouldBe("""{"condition":"new.a == 1","action":{"reject":"Old."}}""");

    [Fact]
    public void Keeping_the_kind_merges_the_action_in_place()
        => Written(
                Parse("""{"action":{"payload":"[1]","type":"webhook","endpoint":"a"}}"""),
                null,
                Parse("""{"type":"webhook","endpoint":"b","payload":"[2]"}"""))
            .ShouldBe("""{"action":{"payload":"[2]","type":"webhook","endpoint":"b"}}""");

    [Fact]
    public void A_payload_the_form_cleared_is_removed()
        => Written(Parse("""{"action":{"type":"webhook","endpoint":"a","payload":"[1]"}}"""), null, Parse("""{"type":"webhook","endpoint":"a"}"""))
            .ShouldBe("""{"action":{"type":"webhook","endpoint":"a"}}""");

    [Fact]
    public void Changing_the_kind_replaces_the_action()
        => Written(
                Parse("""{"action":{"type":"webhook","endpoint":"a","payload":"[1]"}}"""),
                null,
                Parse("""{"type":"email","template":"t","to":"a@b.c"}"""))
            .ShouldBe("""{"action":{"type":"email","template":"t","to":"a@b.c"}}""");

    [Fact]
    public void A_mutate_keeps_its_fields_in_their_order_and_appends_new_ones()
        => Written(Parse("""{"action":{"mutate":{"b":1,"a":2}}}"""), null, Parse("""{"mutate":{"a":3,"c":4,"b":1}}"""))
            .ShouldBe("""{"action":{"mutate":{"b":1,"a":3,"c":4}}}""");

    [Fact]
    public void A_mutate_field_the_form_removed_is_removed()
        => Written(Parse("""{"action":{"mutate":{"a":1,"b":2}}}"""), null, Parse("""{"mutate":{"a":1}}"""))
            .ShouldBe("""{"action":{"mutate":{"a":1}}}""");

    [Fact]
    public void The_original_and_the_action_are_left_as_they_were()
    {
        var original = Parse("""{"condition":"new.a == 1","action":{"reject":"Old."}}""");
        var action = Reject("New.");
        var before = (original.ToJsonString(), action.ToJsonString());

        HookPatch.Apply(original, null, action);

        (original.ToJsonString(), action.ToJsonString()).ShouldBe(before);
    }

    private static string Written(JsonObject? original, string? condition, JsonObject action)
        => HookPatch.Apply(original, condition, action).ToJsonString(Relaxed.Options);

    private static JsonObject Reject(string message) => new() { ["reject"] = message };

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}
