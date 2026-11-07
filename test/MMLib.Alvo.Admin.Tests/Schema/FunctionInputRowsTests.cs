using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>A host function's input form, read from the input JSON Schema its registration exports.</summary>
public class FunctionInputRowsTests
{
    private const string Schema = """
        {"type":"object","properties":{"memo":{"type":"string"},"orderId":{"type":"string"},"amount":{"type":"number"},"note":{"type":["string","null"]}},"required":["orderId","amount","note"]}
        """;

    [Fact]
    public void Required_rows_come_first()
    {
        var rows = FunctionInputRows.From(JsonDocument.Parse(Schema).RootElement);

        rows.Select(row => row.Name).ShouldBe(["amount", "orderId", "memo", "note"]);
        rows.Select(row => row.Required).ShouldBe([true, true, false, false]);
    }

    [Fact]
    public void A_nullable_property_is_optional()
    {
        var note = FunctionInputRows.From(JsonDocument.Parse(Schema).RootElement).Single(row => row.Name == "note");

        note.Required.ShouldBeFalse();
        note.WritesNullWhenEmpty.ShouldBeTrue();
        note.Placeholder.ShouldBe("string or null");
    }

    [Fact]
    public void A_schema_without_properties_has_no_rows() =>
        FunctionInputRows.From(JsonDocument.Parse("""{"type":"object"}""").RootElement).ShouldBeEmpty();

    [Theory]
    [InlineData("integer", "42", "42")]
    [InlineData("number", "12.50", "12.50")]
    [InlineData("boolean", "true", "true")]
    [InlineData("string", "42", "\"42\"")]
    public void A_literal_is_written_as_the_type_it_takes(string type, string text, string json)
    {
        var row = new FunctionInputRow("x", [type], required: true) { Text = text };

        FunctionInputRows.TryLiteral(row, out var value, out _).ShouldBeTrue();
        value!.ToJsonString().ShouldBe(json);
    }
}
