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
    public void Ref_to_built_in_users_entity_is_accepted()
    {
        // "users" is never declared under "entities" (it's schema-reserved as a declared key), but
        // a ref field targeting it is a legitimate reference to the built-in auth entity, not an
        // unknown-entity error — the only thing under test here is that exemption.
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { "fields": {
            "owner": { "type": "ref", "entity": "users" } } } } }
        """;

        var result = _validator.Validate(json);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldNotContain(e => e.Message.Contains("users"));
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

    [Fact]
    public void Keywordless_schema_violation_falls_back_to_a_generic_message()
    {
        // additionalProperties:false has no per-property message from Corvus — this exercises the
        // "Value does not satisfy the schema at ..." fallback in ToError/FixSuggestionFor, not the
        // keyword-supplied message path the other tests hit.
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo", "entities": { "tasks": { "fields": { "title": { "type": "string" } } } },
          "unknownTopLevel": 1 }
        """;

        var result = _validator.Validate(json);

        var error = result.Errors.Single(e => e.Path.Contains("unknownTopLevel"));
        error.Message.ShouldBe("Value does not satisfy the schema at '#/additionalProperties/unknownTopLevel'.");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("'unknownTopLevel'");
    }

    [Fact]
    public void Multiple_simultaneous_schema_violations_are_all_reported_distinctly()
    {
        var json = """{ "apiVersion": "alvo.dev/v2", "entities": {} }""";

        var result = _validator.Validate(json);

        result.Errors.Count.ShouldBeGreaterThanOrEqualTo(2);
        result.Errors.ShouldContain(e => e.Path.Contains("apiVersion"));
        result.Errors.ShouldContain(e => e.Path.Contains("entities"));
        result.Errors.Select(e => e.Message).Distinct().Count().ShouldBe(result.Errors.Count);
    }
}
