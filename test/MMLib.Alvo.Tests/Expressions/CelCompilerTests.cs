using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using System.Globalization;

namespace MMLib.Alvo.Tests.Expressions;

public class CelCompilerTests
{
    private static CelCompilationResult Compile(string source, CelProfile profile = CelProfile.Rule) =>
        CelFixtures._compiler.Compile(source, profile, CelFixtures._orders);

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
    public void Null_source_is_a_contract_violation()
    {
        Should.Throw<ArgumentNullException>(() => CelFixtures._compiler.Compile(null!, CelProfile.Rule, CelFixtures._orders));
    }

    [Fact]
    public void A_pathologically_flat_tree_fails_compilation_instead_of_overflowing_the_stack()
    {
        var source = string.Join(" + ", Enumerable.Range(0, 300).Select(_ => "1"));

        var result = Compile(source);

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].Message.ShouldContain(CelCompiler.MaxTreeDepth.ToString(CultureInfo.InvariantCulture));
    }
}
