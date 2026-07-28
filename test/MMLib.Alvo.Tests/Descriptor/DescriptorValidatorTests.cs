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

    /// <summary>
    /// A field whose name the Data API's query string reserves is refused at <b>apply</b>, one error per
    /// offending field, naming the field and the reserved list.
    /// </summary>
    /// <remarks>
    /// The descriptor's own field grammar accepts every reserved name, so <c>?limit=10</c> against an entity
    /// declaring a <c>limit</c> field is genuinely ambiguous. It is refused here rather than only at route mapping
    /// because the descriptor is wrong whether or not the API is mounted — an embedded host that never maps the
    /// Data API would otherwise get no refusal at all.
    /// </remarks>
    [Theory]
    [InlineData("limit")]
    [InlineData("offset")]
    [InlineData("order")]
    [InlineData("select")]
    [InlineData("after")]
    [InlineData("or")]
    [InlineData("and")]
    [InlineData("not")]
    public void Field_shadowing_a_reserved_query_parameter_is_rejected(string reserved)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "widgets": { "fields": {
            "{{reserved}}": { "type": "integer" } } } } }
        """;

        var result = _validator.Validate(json);

        result.IsValid.ShouldBeFalse();
        var shadowed = result.Errors.ShouldHaveSingleItem();
        shadowed.Path.ShouldBe($"/entities/widgets/fields/{reserved}");
        shadowed.Message.ShouldContain(reserved);
        shadowed.FixSuggestion.ShouldNotBeNull().ShouldContain("Rename the field");
    }

    /// <summary>
    /// A field whose name is merely <em>similar</em> to a reserved one is accepted. Without this the theory above
    /// would pass against a validator that refused every field name it was shown.
    /// </summary>
    [Theory]
    [InlineData("limits")]
    [InlineData("sort_order")]
    [InlineData("selected")]
    [InlineData("Limit")]
    public void Field_merely_resembling_a_reserved_query_parameter_is_accepted(string name)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "widgets": { "fields": {
            "{{name}}": { "type": "integer" } } } } }
        """;

        _validator.Validate(json).Errors.ShouldNotContain(error => error.Message.Contains("reserved"));
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

    [Fact]
    public void A_rule_referencing_an_unknown_column_fails_validation_not_the_request()
    {
        var result = Validate(DescriptorWithRule("list", "ownr_id == @user.id"));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error =>
            error.Path == "/entities/orders/rules/list" && error.Message.Contains("ownr_id"));
    }

    [Fact]
    public void The_singular_user_role_is_refused_at_apply_with_the_plural_fix()
    {
        var result = Validate(DescriptorWithRule("list", "@user.role == 'admin'"));

        result.Errors.ShouldContain(error => error.FixSuggestion!.Contains("in @user.roles"));
    }

    [Fact]
    public void A_row_dependent_hidden_expression_is_refused_at_apply()
    {
        var result = Validate(DescriptorWithHidden("owner_id != @user.id"));

        result.Errors.ShouldContain(error =>
            error.Path == "/entities/orders/fields/notes/hidden");
    }

    [Fact]
    public void A_context_only_hidden_expression_is_accepted()
    {
        Validate(DescriptorWithHidden("'compliance' in @user.roles")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_valid_rule_set_still_validates()
    {
        Validate(DescriptorWithRule("list", "owner_id == @user.id")).IsValid.ShouldBeTrue();
    }

    private static DescriptorValidationResult Validate(string json) => _validator.Validate(json);

    private static string DescriptorWithRule(string operation, string expression) => $$"""
    { "apiVersion": "alvo.dev/v1", "name": "demo",
      "entities": { "orders": {
        "fields": {
          "owner_id": { "type": "uuid" },
          "notes": { "type": "string" }
        },
        "rules": { "{{operation}}": "{{expression}}" }
      } } }
    """;

    private static string DescriptorWithHidden(string expression) => $$"""
    { "apiVersion": "alvo.dev/v1", "name": "demo",
      "auth": { "roles": ["compliance"] },
      "entities": { "orders": {
        "fields": {
          "owner_id": { "type": "uuid" },
          "notes": { "type": "string", "hidden": "{{expression}}" }
        }
      } } }
    """;
}
