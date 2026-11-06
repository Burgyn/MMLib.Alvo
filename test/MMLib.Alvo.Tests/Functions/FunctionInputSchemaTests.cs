using MMLib.Alvo.Functions.Internal;

using System.Text.Json;

namespace MMLib.Alvo.Tests.Functions;

/// <summary>
/// The JSON Schema a function's input type exports: the top-level properties, their JSON types and the required set the
/// apply check reads, from the same serializer options the run binds the input with.
/// </summary>
public sealed class FunctionInputSchemaTests
{
    [Fact]
    public void Required_and_optional_properties_are_read_in_camel_case()
    {
        var schema = FunctionInputSchema.For(typeof(InitOnlyInput));

        schema.Properties.Keys.ShouldBe(["orderId", "amount", "note"], ignoreOrder: true);
        schema.Required.ShouldBe(["orderId"], ignoreOrder: true);
        schema.Properties["orderId"].ShouldBe(JsonTypes.String);
    }

    [Fact]
    public void A_positional_parameter_and_a_non_nullable_string_are_required()
    {
        var schema = FunctionInputSchema.For(typeof(PositionalInput));

        schema.Required.ShouldBe(["orderId", "amount"], ignoreOrder: true);
        schema.Properties["orderId"].ShouldBe(JsonTypes.String, "a non-nullable string accepts no null");
        schema.Document.GetProperty("required").EnumerateArray().Select(name => name.GetString())
            .ShouldContain("orderId", "the exported document says what the binder enforces");
    }

    [Fact]
    public void A_nullable_property_accepts_null()
    {
        var schema = FunctionInputSchema.For(typeof(InitOnlyInput));

        schema.Properties["note"].HasFlag(JsonTypes.Null).ShouldBeTrue();
        schema.Properties["note"].HasFlag(JsonTypes.String).ShouldBeTrue();
        schema.Properties["orderId"].HasFlag(JsonTypes.Null).ShouldBeFalse();
    }

    [Fact]
    public void An_int_property_accepts_a_whole_number_only()
    {
        var schema = FunctionInputSchema.For(typeof(InitOnlyInput));

        schema.Accepts("amount", Literal("3")).ShouldBeTrue();
        schema.Accepts("amount", Literal("3.5")).ShouldBeFalse();
        schema.Accepts("amount", Literal("true")).ShouldBeFalse();
        schema.Accepts("amount", Literal("\"3\"")).ShouldBeTrue("the Web options the binder uses read a number from a string");
        schema.Accepts("note", Literal("null")).ShouldBeTrue();
        schema.Accepts("orderId", Literal("null")).ShouldBeFalse();
    }

    [Fact]
    public void The_binding_options_are_one_shared_instance_that_respects_nullability_and_required_parameters()
    {
        var options = FunctionInputSchema.SerializerOptions;

        options.ShouldBeSameAs(FunctionInputSchema.SerializerOptions);
        options.RespectNullableAnnotations.ShouldBeTrue();
        options.RespectRequiredConstructorParameters.ShouldBeTrue();
        options.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.CamelCase);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<PositionalInput>("""{ "amount": 1 }""", options));
    }

    private static JsonElement Literal(string json) => JsonElement.Parse(json);

    internal sealed class InitOnlyInput
    {
        public required string OrderId { get; init; }

        public int Amount { get; init; }

        public string? Note { get; init; }
    }

    internal sealed record PositionalInput(string OrderId, decimal Amount, string? Note = null);
}
