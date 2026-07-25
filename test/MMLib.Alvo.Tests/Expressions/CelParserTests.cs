using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public class CelParserTests
{
    [Fact]
    public void And_binds_tighter_than_or()
    {
        var parsed = CelParser.Parse("a == 1 || b == 2 && c == 3");

        var root = parsed.ShouldBeOfType<CelBinary>();
        root.Operator.ShouldBe(CelBinaryOperator.Or);
        root.Right.ShouldBeOfType<CelBinary>().Operator.ShouldBe(CelBinaryOperator.And);
    }

    [Fact]
    public void Parentheses_override_precedence()
    {
        var parsed = CelParser.Parse("(a == 1 || b == 2) && c == 3");

        parsed.ShouldBeOfType<CelBinary>().Operator.ShouldBe(CelBinaryOperator.And);
    }

    [Fact]
    public void Negation_applies_to_the_parenthesised_group()
    {
        var parsed = CelParser.Parse("!(owner_id == @user.id)");

        var unary = parsed.ShouldBeOfType<CelUnary>();
        unary.Operator.ShouldBe(CelUnaryOperator.Not);
        unary.Operand.ShouldBeOfType<CelBinary>().Operator.ShouldBe(CelBinaryOperator.Equal);
    }

    [Fact]
    public void Multiplication_binds_tighter_than_addition()
    {
        var parsed = CelParser.Parse("unit_price * amount + 1");

        var root = parsed.ShouldBeOfType<CelBinary>();
        root.Operator.ShouldBe(CelBinaryOperator.Add);
        root.Left.ShouldBeOfType<CelBinary>().Operator.ShouldBe(CelBinaryOperator.Multiply);
    }

    [Fact]
    public void Conditional_is_right_associative()
    {
        var parsed = CelParser.Parse("a ? b : c ? d : e");

        var root = parsed.ShouldBeOfType<CelConditional>();
        root.WhenFalse.ShouldBeOfType<CelConditional>();
    }

    [Fact]
    public void Context_members_become_typed_context_references()
    {
        CelParser.Parse("@user.roles").ShouldBeOfType<CelContextRef>()
            .Value.ShouldBe(CelContextValue.UserRoles);
        CelParser.Parse("@tenant.id").ShouldBeOfType<CelContextRef>()
            .Value.ShouldBe(CelContextValue.TenantId);
    }

    [Fact]
    public void Has_parses_as_a_presence_test_over_a_field()
    {
        CelParser.Parse("has(owner_id)").ShouldBeOfType<CelHas>()
            .Field.FieldName.ShouldBe("owner_id");
    }

    [Fact]
    public void Changed_parses_as_its_own_node()
    {
        CelParser.Parse("changed(status)").ShouldBeOfType<CelChanged>()
            .FieldName.ShouldBe("status");
    }

    [Fact]
    public void New_and_old_prefixes_become_state_qualified_field_references()
    {
        CelParser.Parse("new.status").ShouldBeOfType<CelFieldRef>().State.ShouldBe(CelRecordState.New);
        CelParser.Parse("old.status").ShouldBeOfType<CelFieldRef>().State.ShouldBe(CelRecordState.Old);
    }

    [Theory]
    [InlineData("a ==")]
    [InlineData("== a")]
    [InlineData("a == b == c")]
    [InlineData("(a == b")]
    [InlineData("a && ")]
    [InlineData("has()")]
    [InlineData("has(a, b)")]
    [InlineData("unknown_macro(a)")]
    [InlineData("a.b.c")]
    [InlineData("@user.unknown")]
    [InlineData("[1, 2]")]
    public void Refuses_input_outside_the_grammar(string source)
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));
    }

    [Fact]
    public void Refuses_source_longer_than_the_schema_allows()
    {
        var source = string.Join(" || ", Enumerable.Repeat("a == 1", 400));

        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source))
            .Message.ShouldContain("2000");
    }

    [Fact]
    public void Refuses_pathological_nesting_instead_of_exhausting_the_stack()
    {
        var source = new string('(', 200) + "a" + new string(')', 200);

        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));
    }
}
