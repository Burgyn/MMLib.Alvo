using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A mutate literal as the field's type holds it — the same <c>System.Text.Json</c> calls <c>BeforeHookCompiler.Convert</c>
/// makes (spec B3); Host.Tests holds the two to one answer.
/// </summary>
public class MutateLiteralTests
{
    [Theory]
    [InlineData(FieldType.String, "it's", "\"it's\"")]
    [InlineData(FieldType.String, "", "\"\"")]
    [InlineData(FieldType.Text, "line", "\"line\"")]
    [InlineData(FieldType.Integer, "3", "3")]
    [InlineData(FieldType.Integer, "-2", "-2")]
    [InlineData(FieldType.Decimal, "12.50", "12.50")]
    [InlineData(FieldType.Decimal, "-0.01", "-0.01")]
    [InlineData(FieldType.Boolean, "true", "true")]
    [InlineData(FieldType.DateTime, "2026-10-05T12:00:00Z", "\"2026-10-05T12:00:00Z\"")]
    [InlineData(FieldType.Uuid, "3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f", "\"3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f\"")]
    public void A_literal_the_field_holds_is_written_as_its_json(FieldType type, string text, string json)
    {
        MutateLiteral.TryValue(Row(text), Field(type), out var value, out var refusal).ShouldBeTrue(refusal);

        value!.ToJsonString(Relaxed.Options).ShouldBe(json);
    }

    [Theory]
    [InlineData(FieldType.Integer, "1.5", "whole number")]
    [InlineData(FieldType.Integer, "", "whole number")]
    [InlineData(FieldType.Decimal, "1e3", "number")]
    [InlineData(FieldType.Decimal, "abc", "number")]
    [InlineData(FieldType.Boolean, "yes", "true or false")]
    [InlineData(FieldType.DateTime, "tomorrow", "date and time")]
    [InlineData(FieldType.Uuid, "42", "an id")]
    [InlineData(FieldType.Json, "{}", "json field")]
    public void A_literal_the_field_cannot_hold_is_refused_with_what_it_takes(FieldType type, string text, string says)
    {
        MutateLiteral.TryValue(Row(text), Field(type), out _, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull().ShouldContain(says);
    }

    [Fact]
    public void An_enum_literal_must_be_a_declared_value()
    {
        var status = new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "closed"] };

        MutateLiteral.TryValue(Row("open"), status, out _, out _).ShouldBeTrue();
        MutateLiteral.TryValue(Row("bogus"), status, out _, out var refusal).ShouldBeFalse();
        refusal.ShouldNotBeNull().ShouldContain("open, closed");
    }

    [Fact]
    public void Empty_is_written_as_null_only_into_a_field_that_is_not_required()
    {
        var empty = new MutateRow("f", MutateMode.Literal, string.Empty) { Empty = true };

        MutateLiteral.TryValue(empty, Field(FieldType.String), out var value, out _).ShouldBeTrue();
        value.ShouldBeNull();
        MutateLiteral.TryValue(empty, Field(FieldType.String) with { Required = true }, out _, out var refusal).ShouldBeFalse();
        refusal.ShouldNotBeNull().ShouldContain("required");
    }

    [Theory]
    [InlineData("\"it's\"", "it's", false)]
    [InlineData("12.50", "12.50", false)]
    [InlineData("true", "true", false)]
    [InlineData("null", "", true)]
    public void A_declared_literal_loads_into_a_row_that_writes_it_back(string json, string text, bool empty)
    {
        var row = MutateLiteral.Row("f", JsonNode.Parse(json));

        (row.Field, row.Mode, row.Text, row.Empty).ShouldBe(("f", MutateMode.Literal, text, empty));
    }

    private static MutateRow Row(string text) => new("f", MutateMode.Literal, text);

    private static FieldSchema Field(FieldType type) => new() { Name = "f", Type = type };
}
