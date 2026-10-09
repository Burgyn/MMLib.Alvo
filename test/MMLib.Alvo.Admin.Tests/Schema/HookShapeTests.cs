using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Which hooks the editor may open, and the one sentence a read-only one carries (spec §5.2).</summary>
public class HookShapeTests
{
    [Theory]
    [InlineData("beforeUpdate", """{"condition":"old.a == 1","action":{"reject":"No."}}""")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{"a":{"$cel":"now()"},"b":"x","c":1,"d":true,"e":null}}}""")]
    [InlineData("afterCreate", """{"action":{"type":"webhook","endpoint":"desk"}}""")]
    [InlineData("afterCreate", """{"action":{"type":"webhook","endpoint":"desk","payload":"[{{new.total}}]"}}""")]
    [InlineData("afterUpdate", """{"action":{"type":"email","template":"t","to":"a@b.c"}}""")]
    [InlineData("afterUpdate", """{"action":{"type":"email","template":"t","to":"a@b.c"},"condition":"changed(stage)"}""")]
    public void A_shape_the_editor_writes_is_drawable(string point, string json)
        => HookShape.Undrawable(JsonNode.Parse(json), point).ShouldBeNull();

    [Theory]
    [InlineData("afterUpdate", """{"action":{"type":"entity.update","entity":"x","payload":{}}}""", "'entity.update' is refused")]
    [InlineData("afterUpdate", """{"action":{"type":"function","name":"f"}}""", "'function' is refused")]
    [InlineData("afterUpdate", """{"action":{"type":"http.call","url":"https://x"}}""", "'http.call' is refused")]
    [InlineData("afterUpdate", """{"action":{"type":"email","template":"t","to":"a@b.c","data":"{{new.a}}"}}""", "'data'")]
    [InlineData("afterUpdate", """{"action":{"type":"sms","to":"x"}}""", "not one the schema declares")]
    [InlineData("afterUpdate", """{"action":{"type":"webhook","endpoint":"d","x-note":"y"}}""", "x-note")]
    [InlineData("afterUpdate", """{"action":{"type":"webhook"}}""", "'endpoint' is missing")]
    [InlineData("afterUpdate", """{"action":{"type":"webhook","endpoint":"d","payload":7}}""", "'payload' is not a string")]
    [InlineData("beforeUpdate", """{"condition":"a","action":{"reject":"x"},"x-owner":"ops"}""", "x-owner")]
    [InlineData("beforeUpdate", """{"condition":7,"action":{"reject":"x"}}""", "condition is not a string")]
    [InlineData("beforeUpdate", """{"condition":null,"action":{"reject":"x"}}""", "condition is not a string")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{"prefs":{"a":1}}}}""", "'prefs'")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{"tags":[1,2]}}}""", "'tags'")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{}}}""", "patches no field")]
    [InlineData("beforeDelete", """{"action":{"mutate":{"a":"x"}}}""", "beforeDelete")]
    [InlineData("beforeUpdate", """{"action":{"type":"webhook","endpoint":"d"}}""", "neither a reject nor a mutate")]
    [InlineData("beforeUpdate", """{"action":{"reject":"x","mutate":{"a":"y"}}}""", "more than one of reject and mutate")]
    [InlineData("afterCreate", """{"action":{"reject":"x"}}""", "has no type")]
    [InlineData("afterCreate", """{"action":"x"}""", "action is not an object")]
    [InlineData("afterCreate", """[1]""", "not an object")]
    public void A_shape_the_editor_cannot_write_is_read_only_with_its_reason(string point, string json, string reason)
        => HookShape.Undrawable(JsonNode.Parse(json), point).ShouldNotBeNull().ShouldContain(reason);
}
