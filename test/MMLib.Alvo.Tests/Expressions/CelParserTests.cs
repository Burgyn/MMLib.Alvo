using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using System.Globalization;

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
    public void Context_references_carry_the_accepted_runtime_type()
    {
        CelParser.Parse("@user.id").ShouldBeOfType<CelContextRef>().Type.ShouldBe(CelValueType.Uuid);
        CelParser.Parse("@user.roles").ShouldBeOfType<CelContextRef>().Type.ShouldBe(CelValueType.StringList);
        CelParser.Parse("@tenant.id").ShouldBeOfType<CelContextRef>().Type.ShouldBe(CelValueType.Uuid);
    }

    [Fact]
    public void User_role_suggests_testing_membership_over_roles()
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("@user.role"))
            .FixSuggestion.ShouldNotBeNull().ShouldContain("'editor' in @user.roles");
    }

    [Theory]
    [InlineData("@user.claims")]
    [InlineData("@user.claims['x']")]
    [InlineData("@user.teams")]
    public void User_claims_and_teams_suggest_the_rbac_issue(string source)
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source))
            .FixSuggestion.ShouldNotBeNull().ShouldContain("#37");
    }

    [Fact]
    public void Dotted_macro_style_access_suggests_a_hook_condition()
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("fields.all(f, f > 0)"))
            .FixSuggestion.ShouldNotBeNull().ShouldContain("hooks.beforeUpdate");
    }

    [Fact]
    public void Has_parses_as_a_presence_test_over_a_field()
    {
        CelParser.Parse("has(owner_id)").ShouldBeOfType<CelHas>()
            .Field.FieldName.ShouldBe("owner_id");
    }

    [Fact]
    public void Has_accepts_a_state_qualified_field_path()
    {
        CelParser.Parse("has(new.status)").ShouldBeOfType<CelHas>().Field.State.ShouldBe(CelRecordState.New);
        CelParser.Parse("has(old.status)").ShouldBeOfType<CelHas>().Field.State.ShouldBe(CelRecordState.Old);
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

    [Fact]
    public void Decimal_literal_parses_to_a_decimal_value()
    {
        CelParser.Parse("1.5").ShouldBeOfType<CelLiteral>().Value.ShouldBe(1.5m);
    }

    [Fact]
    public void Oversized_integer_literal_throws_a_syntax_error_not_an_overflow()
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(new string('9', 20)));
    }

    [Fact]
    public void Oversized_decimal_literal_throws_a_syntax_error_not_an_overflow()
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(new string('9', 40) + ".5"));
    }

    [Fact]
    public void List_literal_syntax_suggests_an_equality_chain()
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("status in ['draft', 'review']"))
            .FixSuggestion.ShouldNotBeNull().ShouldContain("status == 'draft' || status == 'review'");
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
    [InlineData("@tenant.unknown")]
    [InlineData("[1, 2]")]
    [InlineData("a[0]")]
    [InlineData("'it''s'")]
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

    [Fact]
    public void Refuses_nesting_past_the_documented_depth_and_says_so()
    {
        var source = new string('(', CelParser.MaxDepth + 1) + "a" + new string(')', CelParser.MaxDepth + 1);

        var exception = Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));

        exception.Message.ShouldContain((CelParser.MaxDepth + 1).ToString(CultureInfo.InvariantCulture));
        exception.Message.ShouldContain(CelParser.MaxDepth.ToString(CultureInfo.InvariantCulture));
        exception.FixSuggestion.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Accepts_exactly_the_documented_depth()
    {
        var source = new string('(', CelParser.MaxDepth) + "a" + new string(')', CelParser.MaxDepth);

        Should.NotThrow(() => CelParser.Parse(source));
    }
}
