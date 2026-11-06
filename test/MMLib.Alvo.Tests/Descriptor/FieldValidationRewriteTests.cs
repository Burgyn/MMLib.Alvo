using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

public sealed class FieldValidationRewriteTests
{
    private static readonly string[] _fields = ["stock", "reserved", "starts_at", "ends_at"];

    [Theory]
    [InlineData("stock", "value >= 0", "stock >= 0")]
    [InlineData("reserved", "value <= new.stock", "reserved <= stock")]
    [InlineData("reserved", "reserved <= stock", "reserved <= stock")]
    [InlineData("ends_at", "!has(starts_at) || value - starts_at <= duration('8h')", "!has(starts_at) || ends_at - starts_at <= duration('28800s')")]
    public void Value_and_new_are_spellings_of_the_row(string field, string source, string canonical) =>
        FieldValidation.Canonical("products", field, source, _fields).ShouldBe(canonical);

    [Theory]
    [InlineData("old.stock >= 0", "old.")]
    [InlineData("changed(stock)", "changed")]
    public void A_transition_is_refused_because_a_check_sees_one_row(string source, string named) =>
        Should.Throw<InvalidDataException>(() => FieldValidation.Canonical("products", "stock", source, _fields)).Message.ShouldContain(named);

    [Fact]
    public void A_bare_value_on_an_entity_with_a_field_named_value_is_refused_as_ambiguous() =>
        Should.Throw<InvalidDataException>(() => FieldValidation.Canonical("readings", "limit", "value > 0", ["value", "limit"]))
            .Message.ShouldSatisfyAllConditions(m => m.ShouldContain("'value'"), m => m.ShouldContain("'limit'"));

    [Fact]
    public void A_conditional_is_refused_with_its_alternative_and_no_parameter_name() =>
        Should.Throw<InvalidDataException>(() => FieldValidation.Canonical("products", "stock", "value > 0 ? true : false", _fields))
            .Message.ShouldSatisfyAllConditions(
                m => m.ShouldContain("'?:'"), m => m.ShouldContain("'&&' and '||'"), m => m.ShouldNotContain("(Parameter"));

    [Fact]
    public void A_field_whose_name_cel_cannot_spell_is_refused_with_no_parameter_name() =>
        Should.Throw<InvalidDataException>(() => FieldValidation.Canonical("products", "true", "value >= 0", ["true"]))
            .Message.ShouldSatisfyAllConditions(
                m => m.ShouldContain("has no canonical CEL text: 'true' is not a CEL field name"), m => m.ShouldNotContain("(Parameter"));

    [Fact]
    public void The_field_named_value_may_validate_itself_by_name() =>
        FieldValidation.Canonical("readings", "value", "value > 0", ["value", "limit"]).ShouldBe("value > 0");
}
