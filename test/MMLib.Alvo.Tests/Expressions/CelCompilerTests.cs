using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Tests.Expressions;

public class CelCompilerTests
{
    private static CelCompilationResult Compile(string source, CelProfile profile = CelProfile.Rule) =>
        CelFixtures.Compiler.Compile(source, profile, CelFixtures.Orders);

    [Fact]
    public void Compiles_a_row_field_compared_to_the_caller()
    {
        var result = Compile("owner_id == @user.id");

        result.IsSuccess.ShouldBeTrue();
        result.Expression!.ResultType.ShouldBe(CelValueType.Bool);
        result.Expression.EntityName.ShouldBe("orders");
    }

    [Fact]
    public void Compiles_role_membership()
    {
        Compile("'editor' in @user.roles").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_unknown_column_fails_at_compile_time_with_the_known_fields()
    {
        var result = Compile("ownr_id == @user.id");

        result.IsSuccess.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem();
        result.Errors[0].Message.ShouldContain("ownr_id");
        result.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain("owner_id");
    }

    [Fact]
    public void The_singular_user_role_is_rejected_with_the_plural_fix()
    {
        var result = Compile("@user.role == 'editor'");

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain("'editor' in @user.roles");
    }

    [Fact]
    public void Comparing_the_role_list_to_a_string_is_a_type_error_not_a_contains()
    {
        var result = Compile("@user.roles == 'editor'");

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain(" in @user.roles");
    }

    [Fact]
    public void Claims_are_rejected_and_point_at_the_rbac_issue()
    {
        var result = Compile("@user.claims['department'] == status");

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain("#37");
    }

    [Theory]
    [InlineData("status == 1")]
    [InlineData("total == 'x'")]
    [InlineData("owner_id < @user.id")]
    [InlineData("status && total")]
    [InlineData("payload == 'x'")]
    [InlineData("!status")]
    [InlineData("status")]
    public void Type_errors_are_reported_at_compile_time(string source)
    {
        Compile(source).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void A_rule_must_evaluate_to_a_boolean()
    {
        Compile("total").IsSuccess.ShouldBeFalse();
        Compile("true").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Every_error_carries_a_position_so_an_agent_can_point_at_the_source()
    {
        var result = Compile("owner_id == @user.id && ownr_id == @user.id");

        result.Errors[0].Position.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void Two_independent_errors_are_both_collected_from_one_source()
    {
        var result = Compile("ownr_id == @user.id && total == 'x'");

        result.IsSuccess.ShouldBeFalse();
        result.Errors.Count.ShouldBe(2);
    }

    [Fact]
    public void Each_non_boolean_operand_of_a_logical_operator_gets_its_own_error_position()
    {
        var result = Compile("status && total");

        result.Errors.Count.ShouldBe(2);
        result.Errors[0].Position.ShouldNotBe(result.Errors[1].Position);
    }

    [Fact]
    public void Each_non_numeric_operand_of_an_arithmetic_operator_gets_its_own_error_position()
    {
        var result = CelFixtures.Compiler.Compile("status + owner_id", CelProfile.Computed, CelFixtures.Orders);

        result.Errors.Count.ShouldBe(2);
        result.Errors[0].Position.ShouldNotBe(result.Errors[1].Position);
    }

    [Theory]
    [InlineData("status == 'aproved'")]
    [InlineData("status != 'aproved'")]
    public void An_undeclared_enum_value_fails_at_compile_time_with_a_suggestion(string source)
    {
        var result = Compile(source);

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain("approved");
    }

    [Fact]
    public void Ternary_branches_must_have_the_same_type()
    {
        var result = CelFixtures.Compiler.Compile("status == 'draft' ? 1 : 'x'", CelProfile.Computed, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("'x' in status")]
    [InlineData("total in @user.roles")]
    public void The_in_operator_requires_a_string_and_a_role_list(string source)
    {
        Compile(source).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Relational_operators_reject_boolean_operands()
    {
        Compile("true < false").IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Relational_operators_reject_null()
    {
        Compile("total < null").IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Arithmetic_on_a_non_numeric_operand_fails_at_compile_time()
    {
        var result = CelFixtures.Compiler.Compile("total + status", CelProfile.Computed, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_field_inside_has_fails_at_compile_time()
    {
        Compile("has(nope)").IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_field_inside_changed_fails_at_compile_time()
    {
        CelFixtures.Compiler.Compile("changed(nope)", CelProfile.Condition, CelFixtures.Orders)
            .IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void An_unmapped_field_type_fails_compilation_instead_of_throwing()
    {
        var schema = new EntitySchema
        {
            Name = "weird",
            Fields = [new FieldSchema { Name = "mystery", Type = (FieldType)999 }],
        };

        var result = CelFixtures.Compiler.Compile("mystery == mystery", CelProfile.Rule, schema);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Null_source_is_a_contract_violation()
    {
        Should.Throw<ArgumentNullException>(() => CelFixtures.Compiler.Compile(null!, CelProfile.Rule, CelFixtures.Orders));
    }

    [Fact]
    public void A_pathologically_flat_tree_fails_compilation_instead_of_overflowing_the_stack()
    {
        var source = string.Join(" + ", Enumerable.Range(0, 300).Select(_ => "1"));

        var result = CelFixtures.Compiler.Compile(source, CelProfile.Computed, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].Message.ShouldContain(CelCompiler.MaxTreeDepth.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_depth_cap_accepts_exactly_the_documented_depth_and_rejects_one_more()
    {
        var atLimit = string.Join(" || ", Enumerable.Repeat("true", CelCompiler.MaxTreeDepth));
        var overLimit = string.Join(" || ", Enumerable.Repeat("true", CelCompiler.MaxTreeDepth + 1));

        Compile(atLimit).IsSuccess.ShouldBeTrue();
        Compile(overLimit).IsSuccess.ShouldBeFalse();
    }
}
