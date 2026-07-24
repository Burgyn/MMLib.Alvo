using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

public class DescriptorValidatorTests
{
    private static readonly DescriptorValidator _validator = new();

    [Fact]
    public void Valid_descriptor_passes()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "tasks": { "fields": { "title": { "type": "string" } } } } }
        """;

        _validator.Validate(json).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Schema_violation_is_a_structured_error()
    {
        // 'name' is required by the schema; omit it.
        var json = """{ "apiVersion": "alvo.dev/v1", "entities": {} }""";

        var result = _validator.Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Severity == DescriptorValidationSeverity.Error);
        result.Errors.ShouldAllBe(e => e.Message.Length > 0);
    }

    [Fact]
    public void Computed_field_is_rejected_with_fix_suggestion()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "fields": {
            "gross": { "type": "decimal", "precision": 18, "scale": 2, "computed": "net * 1.2" } } } } }
        """;

        var result = _validator.Validate(json);

        var computed = result.Errors.ShouldHaveSingleItem();
        computed.Path.ShouldContain("gross");
        computed.FixSuggestion.ShouldNotBeNull().ShouldContain("#21");
    }

    [Fact]
    public void Ref_to_unknown_entity_is_rejected()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { "fields": {
            "customer": { "type": "ref", "entity": "missing" } } } } }
        """;

        var result = _validator.Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Message.Contains("missing"));
    }

    [Fact]
    public void Malformed_json_is_a_single_structured_error()
    {
        var json = """{ "apiVersion": "alvo.dev/v1", "name": "demo", """;

        var result = _validator.Validate(json);

        var error = result.Errors.ShouldHaveSingleItem();
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
        error.Path.ShouldBe("/");
        error.Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public void Schema_violations_carry_distinct_non_constant_fix_suggestions()
    {
        var missingName = """
        { "apiVersion": "alvo.dev/v1",
          "entities": { "tasks": { "fields": { "title": { "type": "string" } } } } }
        """;
        var missingApiVersion = """
        { "name": "demo",
          "entities": { "tasks": { "fields": { "title": { "type": "string" } } } } }
        """;

        var nameError = _validator.Validate(missingName).Errors.ShouldHaveSingleItem();
        var apiVersionError = _validator.Validate(missingApiVersion).Errors.ShouldHaveSingleItem();

        nameError.FixSuggestion.ShouldNotBeNull();
        apiVersionError.FixSuggestion.ShouldNotBeNull();
        nameError.FixSuggestion.ShouldNotBe(apiVersionError.FixSuggestion);
        nameError.FixSuggestion.ShouldContain("required");
    }
}
