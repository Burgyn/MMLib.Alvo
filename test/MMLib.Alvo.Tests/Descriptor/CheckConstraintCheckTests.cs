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
