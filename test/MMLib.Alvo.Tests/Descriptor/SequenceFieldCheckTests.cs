using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

public sealed class SequenceFieldCheckTests
{
    private const string Pointer = "/entities/orders/fields/number/sequence";
    private const string Branch = "\"branch\": {\"type\": \"string\", \"required\": true}";

    private static IReadOnlyList<DescriptorValidationError> Check(string fieldsJson, string storage = "physical") =>
        SequenceFieldCheck.Check(AlvoDescriptor.Parse(Descriptors.WithEntity("orders", fieldsJson, storage)));

    private static IReadOnlyList<DescriptorValidationError> Validate(string fieldsJson, string storage = "physical") =>
        [.. new DescriptorValidator().Validate(Descriptors.WithEntity("orders", fieldsJson, storage)).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error)];

    private static string Number(string type, string sequence, string extra = "") =>
        $$$"""{{{{Branch}}}, "qty": {"type": "integer"}, "number": {"type": "{{{type}}}"{{{extra}}}, "sequence": {{{sequence}}}}}""";

    [Fact]
    public void A_valid_sequence_has_no_findings()
    {
        Validate(Number("integer", """{"per": "branch", "period": "year", "start": 1000}""")).ShouldBeEmpty();
        Validate(Number("string", """{"format": "SO-{yyyy}-{n:4}"}""")).ShouldBeEmpty();
    }

    [Fact]
    public void A_sequence_on_a_decimal_field_is_refused()
    {
        var error = Check(Number("decimal", "{}", """, "precision": 10, "scale": 0""")).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldBe("Field 'orders.number' declares 'sequence', which only an integer or a string field can hold.");
    }

    [Fact]
    public void A_string_sequence_without_format_is_refused()
    {
        var error = Check(Number("string", "{}")).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldBe("Field 'orders.number' declares a string 'sequence' without 'format', so there is no text to store the number in.");
    }

    [Fact]
    public void An_integer_sequence_with_format_is_refused()
    {
        var error = Check(Number("integer", """{"format": "{n}"}""")).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/format");
        error.Message.ShouldBe("Field 'orders.number' declares a 'format' on an integer 'sequence', which stores the number itself.");
    }

    [Theory]
    [InlineData(""", "default": 7""", "default")]
    [InlineData(", \"computed\": \"qty + 1\"", "computed")]
    [InlineData(""", "rollup": {"from": "orders", "op": "count", "via": "number"}""", "rollup")]
    [InlineData(""", "maxLength": 20""", "maxLength")]
    [InlineData(", \"format\": \"email\"", "format")]
    public void A_sequence_with_a_default_computed_rollup_or_maxLength_is_refused(string extra, string keyword)
    {
        var type = keyword is "maxLength" or "format" ? "string" : "integer";
        var sequence = type == "string" ? """{"format": "{n}"}""" : "{}";

        var error = Check(Number(type, sequence, extra)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldBe($"Field 'orders.number' declares both 'sequence' and '{keyword}', but the framework assigns a sequence's value.");
    }

    [Theory]
    [InlineData("missing", "names no field of 'orders'")]
    [InlineData("number", "names the sequence itself")]
    [InlineData("qty", "names a field that is not required")]
    public void A_per_naming_no_field_itself_or_an_optional_field_is_refused(string per, string reason)
    {
        var error = Check(Number("integer", $$"""{"per": "{{per}}"}""")).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/per");
        error.Message.ShouldBe($"Field 'orders.number' counts its 'sequence' per '{per}', which {reason}.");
    }

    [Theory]
    [InlineData("{\"type\": \"string\", \"required\": true, \"computed\": \"branch\"}")]
    [InlineData("{\"type\": \"integer\", \"required\": true, \"rollup\": {\"from\": \"lines\", \"op\": \"count\", \"via\": \"order_id\"}}")]
    [InlineData("{\"type\": \"integer\", \"required\": true, \"sequence\": {}}")]
    public void A_per_naming_a_field_the_framework_maintains_is_refused(string kind)
    {
        var fields = "{" + Branch + ", \"number\": {\"type\": \"integer\", \"sequence\": {\"per\": \"kind\"}}, \"kind\": " + kind + "}";

        var error = Check(fields).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/per");
        error.Message.ShouldBe(
            "Field 'orders.number' counts its 'sequence' per 'kind', which is maintained by the framework and has no value when the number is allocated.");
    }

    [Fact]
    public void A_per_naming_a_virtual_field_is_refused()
    {
        var fields = "{" + Branch + ", \"number\": {\"type\": \"integer\", \"sequence\": {\"per\": \"kind\"}}, \"kind\": {\"type\": \"string\", \"virtual\": true}}";

        var error = Check(fields).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/per");
        error.Message.ShouldBe(
            "Field 'orders.number' counts its 'sequence' per 'kind', which is virtual and has no value when the number is allocated.");
    }

    [Theory]
    [InlineData("json", "")]
    [InlineData("decimal", """, "precision": 10, "scale": 2""")]
    [InlineData("datetime", "")]
    public void A_per_of_type_json_decimal_or_datetime_is_refused(string type, string facets)
    {
        var fields = $$"""{"kind": {"type": "{{type}}", "required": true{{facets}}}, """
            + "\"number\": {\"type\": \"integer\", \"sequence\": {\"per\": \"kind\"}}}";

        var error = Check(fields).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/per");
        error.Message.ShouldBe($"Field 'orders.number' counts its 'sequence' per 'kind', which is a {type} field and has no value to count by.");
    }

    [Theory]
    [InlineData("{n}-{n}")]
    [InlineData("SO-{q}-{n}")]
    [InlineData("SO-{yyyy}")]
    [InlineData("{n:19}")]
    [InlineData("SO-{n")]
    public void A_format_with_two_numbers_or_an_unknown_token_is_refused(string format)
    {
        var error = Check(Number("string", $$"""{"format": "{{format}}"}""")).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/format");
        error.Message.ShouldBe($"Field 'orders.number' declares the 'sequence' format '{format}', which needs exactly one {{n}} or {{n:width}} and no braces other than {{yyyy}}, {{yy}}, {{MM}} and {{dd}}.");
    }

    [Fact]
    public void A_sequence_on_a_dynamic_entity_is_refused_naming_41()
    {
        var error = Validate("""{"number": {"type": "integer", "sequence": {}}}""", storage: "dynamic").ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldBe(
            "Field 'orders.number' declares 'sequence', which the framework counts in a table of its own, and 'orders' declares "
            + "'storage: dynamic' — dynamic entities have no table to hold one until #41 defines how their integrity is enforced.");
        error.FixSuggestion.ShouldBe("Remove 'sequence', or make 'orders' a physical entity.");
    }

    [Fact]
    public void The_mapper_refuses_a_sequence_on_a_dynamic_entity_naming_41() =>
        Should.Throw<InvalidDataException>(() => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(
                Descriptors.WithEntity("orders", """{"number": {"type": "integer", "sequence": {}}}""", "dynamic"))))
            .Message.ShouldContain("#41");

    [Fact]
    public void The_mapper_resolves_start_and_period()
    {
        var model = DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(Descriptors.WithEntity(
            "orders", Number("string", """{"per": "branch", "period": "month", "format": "{n}"}"""), "physical")));

        var sequence = model.Entities.Single().Fields.Single(field => field.Name == "number").Sequence.ShouldNotBeNull();
        sequence.ShouldBe(new MMLib.Alvo.Schema.SequenceSchema
        {
            Per = "branch",
            Period = MMLib.Alvo.Schema.SequenceResetPeriod.Month,
            Start = 1,
            Format = "{n}",
        });
    }
}
