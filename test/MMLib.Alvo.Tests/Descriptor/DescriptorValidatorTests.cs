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
    /// Every field feature the frozen schema declares and this build does not honour is reported here as a
    /// <b>structured</b> error — a JSON path, what silently happens instead, and what to do about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapper refuses the same four with an exception, which is the guard an embedded host that never
    /// validates still passes through; this pass is what makes the refusal agent-first, and it is the only one
    /// a dashboard or a CLI <c>validate</c> can show. Three of the four were reported by neither: a
    /// <c>validation</c> expression was simply dropped, so a value it forbade came back 201.
    /// </para>
    /// <para>
    /// The message is asserted to name the <em>consequence</em> and not merely the word "unsupported" — an
    /// author who is told "not supported yet" removes the key and moves on, while one who is told the field is
    /// therefore unconstrained knows what they lost.
    /// </para>
    /// </remarks>
    /// <param name="declaration">How the field declares the feature.</param>
    /// <param name="messageMentions">A word the message must carry, describing what happens instead.</param>
    /// <param name="fixMentions">A word the fix suggestion must carry.</param>
    [Theory]
    [InlineData(@"""computed"": ""net * 1.2""", "never evaluated", "#21")]
    [InlineData(@"""rollup"": { ""from"": ""lines"", ""op"": ""count"" }", "maintains", "query")]
    [InlineData(@"""validation"": ""value >= 0""", "not constrained", "before-hook")]
    [InlineData(@"""default"": 1", "NOT NULL", "explicitly")]
    public void Every_unhonoured_field_feature_is_a_structured_error(
        string declaration, string messageMentions, string fixMentions)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": {
            "lines": { "fields": { "invoice_id": { "type": "uuid" } } },
            "invoices": { "fields": {
              "amount": { "type": "decimal", "precision": 18, "scale": 2, {{declaration}} } } } } }
        """;

        var result = _validator.Validate(json);

        var error = result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/invoices/fields/amount");
        error.Message.ShouldContain(messageMentions);
        error.FixSuggestion.ShouldNotBeNull().ShouldContain(fixMentions);
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
    }

    /// <summary>
    /// A field declaring two of them reports <b>both</b>, not the first — the same every-violation promise
    /// the Data API keeps, one layer down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapper stops at the first, because an exception can only carry one; that is why this pass exists
    /// beside it rather than only behind it. An author fixing them one apply at a time pays a round trip per
    /// key.
    /// </para>
    /// <para>
    /// Asserted by <em>which</em> refusals are present rather than by a total count. This descriptor is
    /// deliberately schema-valid, but a count would still couple the fact to however many findings any other
    /// pass happens to produce for the same field — and that coupling is what makes a fact fail for a reason
    /// its name does not claim. <c>computed</c> is left out for the same reason: combined with <c>default</c>
    /// it is refused by the schema pass too, which is a different statement.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_field_declaring_several_unhonoured_features_reports_all_of_them()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "fields": {
            "amount": {
              "type": "decimal", "precision": 18, "scale": 2,
              "validation": "value >= 0", "default": 1 } } } } }
        """;

        var result = _validator.Validate(json);

        var atTheField = result.Errors
            .Where(error => error.Path == "/entities/invoices/fields/amount")
            .ToList();
        atTheField.ShouldContain(
            error => error.Message.Contains("not constrained", StringComparison.Ordinal),
            "the 'validation' refusal must be reported");
        atTheField.ShouldContain(
            error => error.Message.Contains("NOT NULL", StringComparison.Ordinal),
            "and the 'default' refusal beside it — reporting only the first costs an apply per key");
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
