using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

public sealed class CheckConstraintCheckTests
{
    private static IReadOnlyList<DescriptorValidationError> Validate(string fieldsJson, string storage = "physical") =>
        [.. new DescriptorValidator().Validate(Descriptors.WithEntity("products", fieldsJson, storage)).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error)];

    private static MMLib.Alvo.Schema.SchemaModel Map(string fieldsJson, string storage = "physical") =>
        DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(Descriptors.WithEntity("products", fieldsJson, storage)));

    [Fact]
    public void A_validation_over_the_row_is_valid() =>
        Validate("""{"stock": {"type": "integer", "validation": "value >= 0"}}""").ShouldBeEmpty();

    [Theory]
    [InlineData("datetime", "value <= now()", "now()")]
    [InlineData("uuid", "value == @user.id", "@user")]
    [InlineData("integer", "'admin' in @user.roles", "role")]
    [InlineData("integer", "value * 2 > 4", "Arithmetic")]
    [InlineData("integer", "value > ", "")]
    [InlineData("integer", "missing > 0", "missing")]
    public void A_refused_validation_is_reported_at_its_pointer(string type, string expression, string named)
    {
        var error = Validate($$$"""{"stock": {"type": "{{{type}}}", "validation": "{{{expression}}}"}}""").ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldContain(named);
    }

    [Theory]
    [InlineData("value >= -40")]
    [InlineData("value <= 500")]
    public void A_negative_bound_and_a_validation_on_a_computed_field_are_valid(string expression) =>
        Validate($$$"""{"qty": {"type": "integer"}, "stock": {"type": "integer", "computed": "qty + qty", "validation": "{{{expression}}}"}}""").ShouldBeEmpty();

    [Theory]
    [InlineData("a\\u0001b")]
    [InlineData("a\\u2028b")]
    public void A_text_constant_a_sql_literal_cannot_carry_is_refused_at_apply(string text)
    {
        var error = Validate($$$"""{"stock": {"type": "string", "validation": "value != '{{{text}}}'"}}""").ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldContain("cannot be written into a CHECK constraint");
    }

    /// <summary>
    /// A validation reading another field is refused when either side is hidden — static or per role — because the 422 or
    /// success on an update would let a caller bisect a hidden stored value (HX-11 Ruling 61, spec H1 for validations).
    /// </summary>
    /// <param name="fieldsJson">The entity's fields.</param>
    /// <param name="validated">The field whose validation is refused.</param>
    [Theory]
    [InlineData("""{"margin": {"type": "integer", "hidden": true}, "discount": {"type": "integer", "validation": "value <= margin"}}""", "discount")]
    [InlineData("""{"price": {"type": "integer"}, "margin": {"type": "integer", "hidden": true, "validation": "value <= price"}}""", "margin")]
    [InlineData("""{"margin": {"type": "integer", "hidden": "!('admin' in @user.roles)"}, "discount": {"type": "integer", "validation": "value <= margin"}}""", "discount")]
    [InlineData("""{"margin": {"type": "integer", "hidden": true}, "discount": {"type": "integer", "validation": "value <= new.margin"}}""", "discount")]
    [InlineData("""{"margin": {"type": "integer", "hidden": true}, "discount": {"type": "integer", "validation": "!has(margin) || value <= 5"}}""", "discount")]
    [InlineData("""{"price": {"type": "integer"}, "margin": {"type": "integer", "hidden": true}, "total": {"type": "integer", "computed": "price + margin", "hidden": true, "validation": "value <= 1000"}}""", "total")]
    [InlineData("""{"price": {"type": "integer"}, "margin": {"type": "integer", "hidden": true}, "total": {"type": "integer", "computed": "price + margin", "validation": "value <= 1000"}}""", "total")]
    [InlineData("""{"price": {"type": "integer"}, "margin": {"type": "integer", "hidden": true}, "net": {"type": "integer", "computed": "price + margin"}, "discount": {"type": "integer", "validation": "value <= net"}}""", "discount")]
    public void A_validation_reading_across_a_hidden_field_is_refused(string fieldsJson, string validated)
    {
        var error = Validate(fieldsJson).ShouldHaveSingleItem();
        error.Path.ShouldBe($"/entities/products/fields/{validated}/validation");
        error.Message.ShouldContain("hidden");
        error.FixSuggestion!.ShouldContain("before-hook");
    }

    /// <summary>
    /// A rollup reads its child's field, so a parent validation reaching a hidden field through one — the child's aggregated
    /// field, or a hidden parent field beside the rollup — is refused like a direct read (Ruling 61).
    /// </summary>
    /// <param name="parentFieldsJson">The parent's fields besides its rollup.</param>
    /// <param name="childFieldJson">The child's aggregated field.</param>
    /// <param name="rollupJson">The parent's rollup field.</param>
    [Theory]
    [InlineData("""{}""", """{"type": "integer", "hidden": true}""",
        """{"type": "integer", "hidden": true, "rollup": {"from": "lines", "op": "sum", "field": "amount"}, "validation": "value <= 100"}""")]
    [InlineData("""{"stock": {"type": "integer", "hidden": true}}""", """{"type": "integer"}""",
        """{"type": "integer", "rollup": {"from": "lines", "op": "sum", "field": "amount"}, "validation": "value <= stock"}""")]
    public void A_validation_reaching_a_hidden_field_through_a_rollup_is_refused(string parentFieldsJson, string childFieldJson, string rollupJson)
    {
        var error = ValidateBins(parentFieldsJson, childFieldJson, rollupJson).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/bins/fields/reserved/validation");
        error.Message.ShouldContain("hidden");
    }

    /// <summary>
    /// A rollup's <c>where</c> reads the child too, so a parent validation whose rollup filters on a hidden child field is a
    /// hidden read — for a <c>count</c> as much as a <c>sum</c>: which rows are counted would disclose the hidden value (Ruling 61).
    /// </summary>
    /// <param name="op">The rollup's operation.</param>
    [Theory]
    [InlineData("count")]
    [InlineData("sum")]
    public void A_validation_reaching_a_hidden_child_field_through_a_rollup_filter_is_refused(string op)
    {
        var field = op == "count" ? string.Empty : ", \"field\": \"amount\"";
        var error = ValidateBins(
            """{}""",
            """{"type": "integer"}""",
            $$"""{"type": "integer", "rollup": {"from": "lines", "op": "{{op}}"{{field}}, "where": "flag == 'on'"}, "validation": "value <= 100"}""",
            childExtraJson: """, "flag": {"type": "string", "hidden": true}""").ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/bins/fields/reserved/validation");
        error.Message.ShouldContain("'lines.flag'");
    }

    /// <summary>The counterweight: a filter over a visible child field adds a visible read, and the validation stays legal.</summary>
    [Fact]
    public void A_validation_through_a_rollup_filtering_on_a_visible_child_field_is_valid() =>
        ValidateBins(
            """{}""",
            """{"type": "integer"}""",
            """{"type": "integer", "rollup": {"from": "lines", "op": "sum", "field": "amount", "where": "flag == 'on'"}, "validation": "value <= 100"}""",
            childExtraJson: """, "flag": {"type": "string"}""").ShouldBeEmpty();

    /// <summary>The counterweight: a rollup over a visible child field, validated against itself, is legal.</summary>
    [Fact]
    public void A_rollup_over_a_visible_child_field_validated_against_itself_is_valid() =>
        ValidateBins("""{}""", """{"type": "integer"}""",
            """{"type": "integer", "rollup": {"from": "lines", "op": "sum", "field": "amount"}, "validation": "value <= 100"}""").ShouldBeEmpty();

    /// <summary>A computed field over visible fields, validated against itself, reads nothing hidden and stays legal.</summary>
    [Fact]
    public void A_computed_field_over_visible_fields_validated_against_itself_is_valid() =>
        Validate("""{"price": {"type": "integer"}, "tax": {"type": "integer"}, "total": {"type": "integer", "computed": "price + tax", "validation": "value <= 1000"}}""")
            .ShouldBeEmpty();

    /// <summary>The hidden-read refusal is reported beside the validation's other refusals, not instead of them.</summary>
    [Fact]
    public void A_hidden_read_is_reported_beside_the_validation_s_other_refusals()
    {
        var errors = Validate("""{"secret": {"type": "string", "hidden": true}, "code": {"type": "string", "validation": "value != secret && value != 'a\u0001b'"}}""");

        errors.Count.ShouldBe(2, string.Join(" | ", errors.Select(error => error.Message)));
        errors.ShouldAllBe(error => error.Path == "/entities/products/fields/code/validation");
        errors.ShouldContain(error => error.Message.Contains("hidden"));
        errors.ShouldContain(error => error.Message.Contains("cannot be written into a CHECK constraint"));
    }

    private static IReadOnlyList<DescriptorValidationError> ValidateBins(
        string parentFieldsJson, string childFieldJson, string rollupJson, string childExtraJson = "")
    {
        var parent = parentFieldsJson.Trim()[..^1];
        var parentFields = parent.Length > 1 ? $"{parent}, \"reserved\": {rollupJson}}}" : $"{{\"reserved\": {rollupJson}}}";
        const string Rules = """{"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}""";
        var json = "{\"$schema\": \"https://alvo.dev/schema/v1/project.json\", \"apiVersion\": \"alvo.dev/v1\", \"name\": \"checks\", \"entities\": {"
            + $"\"bins\": {{\"fields\": {parentFields}, \"rules\": {Rules}}}, "
            + $"\"lines\": {{\"fields\": {{\"bin_id\": {{\"type\": \"ref\", \"entity\": \"bins\", \"required\": true}}, \"amount\": {childFieldJson}{childExtraJson}}}, \"rules\": {Rules}}}}}}}";
        return [.. new DescriptorValidator().Validate(json).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];
    }

    /// <summary>A stored field's validation over its own value only discloses nothing the caller did not send (Ruling 61).</summary>
    [Fact]
    public void A_hidden_field_validated_against_itself_alone_is_valid() =>
        Validate("""{"margin": {"type": "integer", "hidden": "!('admin' in @user.roles)", "validation": "value >= 0"}}""").ShouldBeEmpty();

    /// <summary>A static <c>hidden: false</c> hides nothing, so the cross-field validation stays legal.</summary>
    [Fact]
    public void A_field_declared_not_hidden_may_be_read_across() =>
        Validate("""{"margin": {"type": "integer", "hidden": false}, "discount": {"type": "integer", "validation": "value <= margin"}}""").ShouldBeEmpty();

    [Fact]
    public void A_validation_on_a_dynamic_entity_is_refused_naming_41()
    {
        var error = Validate("""{"stock": {"type": "integer", "validation": "value >= 0"}}""", storage: "dynamic")
            .ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldContain("#41");
    }

    [Fact]
    public void The_mapper_carries_the_canonical_predicate_over_the_row() =>
        Map("""{"qty": {"type": "integer"}, "stock": {"type": "integer", "validation": "value >= new.qty"}}""")
            .Entities.Single().Fields.Single(field => field.Name == "stock").CheckExpression.ShouldBe("stock >= qty");

    [Fact]
    public void The_mapper_refuses_a_validation_on_a_dynamic_entity_naming_41() =>
        Should.Throw<InvalidDataException>(() => Map("""{"stock": {"type": "integer", "validation": "value >= 0"}}""", "dynamic"))
            .Message.ShouldBe(FieldValidation.DynamicMessage("products", "stock"));

    [Fact]
    public void A_validation_is_no_longer_an_unhonoured_feature() =>
        UnhonouredFeatures.EveryRefusal.ShouldNotContain(refusal => refusal.Slot == "field.validation");
}

internal static class Descriptors
{
    internal static string WithEntity(string entity, string fieldsJson, string storage) =>
        $$"""{"$schema": "https://alvo.dev/schema/v1/project.json", "apiVersion": "alvo.dev/v1", "name": "checks", "entities": {"{{entity}}": {"storage": "{{storage}}", "fields": {{fieldsJson}},"""
        + """ "rules": {"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}}}}""";
}
