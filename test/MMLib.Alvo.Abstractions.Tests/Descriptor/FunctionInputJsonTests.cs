using MMLib.Alvo.Descriptor;
using System.Text.Json;

namespace MMLib.Alvo.Abstractions.Tests.Descriptor;

public class FunctionInputJsonTests
{
    private const string ObjectInput = """{ "orderId": "{{new.id}}", "amount": { "$cel": "new.total" }, "rush": true }""";

    [Fact]
    public void An_object_input_round_trips()
    {
        var action = FunctionOf(Descriptor(ObjectInput));

        action.Input.ShouldNotBeNull();
        action.Input.Transform.ShouldBeNull();
        var fields = action.Input.Fields.ShouldNotBeNull();
        fields["orderId"]!.Literal!.Value.GetString().ShouldBe("{{new.id}}");
        fields["amount"]!.Expression.ShouldBe("new.total");
        fields["rush"]!.Literal!.Value.GetBoolean().ShouldBeTrue();

        var reparsed = FunctionOf(AlvoDescriptor.Serialize(Descriptor(ObjectInput)));
        reparsed.Input!.Fields!["amount"]!.Expression.ShouldBe("new.total");
        reparsed.Input.Fields["orderId"]!.Literal!.Value.GetString().ShouldBe("{{new.id}}");
    }

    [Fact]
    public void A_string_input_round_trips()
    {
        var action = FunctionOf(Descriptor("\"{ 'id': new.id }\""));

        action.Input!.Transform.ShouldBe("{ 'id': new.id }");
        action.Input.Fields.ShouldBeNull();

        var reparsed = FunctionOf(AlvoDescriptor.Serialize(Descriptor("\"{ 'id': new.id }\"")));
        reparsed.Input!.Transform.ShouldBe("{ 'id': new.id }");
    }

    [Fact]
    public void A_null_input_property_reads_as_the_null_literal()
    {
        var action = FunctionOf(Descriptor("""{ "note": null }"""));

        var note = action.Input!.Fields!["note"].ShouldNotBeNull(
            "a JSON null input value is the null literal it was written as, so no C# null reaches a compiler");
        note.IsExpression.ShouldBeFalse();
        note.Literal!.Value.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static AlvoDescriptor Descriptor(string input) => AlvoDescriptor.Parse($$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "orders": {
              "fields": { "total": { "type": "decimal" } },
              "hooks": {
                "afterCreate": [
                  { "action": { "type": "function", "name": "issue-invoice", "input": {{input}} } }
                ]
              }
            }
          }
        }
        """);

    private static FunctionAction FunctionOf(string json) => FunctionOf(AlvoDescriptor.Parse(json));

    private static FunctionAction FunctionOf(AlvoDescriptor descriptor) =>
        descriptor.Entities["orders"].Hooks!.AfterCreate![0].Action.ShouldBeOfType<FunctionAction>();
}
