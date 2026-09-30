using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The fail-fast half of "what the checker accepts is what the renderer renders" for the two profiles
/// <see cref="SqlPredicateRenderer"/> turns into SQL — <see cref="CelProfile.Rule"/> and
/// <see cref="CelProfile.Computed"/>. Each shape here type-checks (both operands have the same type) but has
/// no SQL form, so before this refusal it compiled at save time and threw on every request instead.
/// </summary>
public class CelSqlRenderableShapeTests
{
    private static readonly SqlPredicateRenderer _renderer = new();
    private static readonly TestFieldSqlRenderer _fields = new();

    [Theory]
    [InlineData("(total > 5.0) == true")]
    [InlineData("has(title) == is_public")]
    [InlineData("(is_public && is_public) == is_public")]
    [InlineData("!(is_public) == is_public")]
    [InlineData("true != (title == 'x')")]
    public void A_rule_comparing_a_predicate_is_refused_at_compile_time_toward_the_direct_comparison(string source)
    {
        var result = CelFixtures.Compiler.Compile(source, CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Message.ShouldContain("field, a literal or a context value");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("total > 5");
    }

    [Theory]
    [InlineData("(total + 1.0) > 100.0 ? total : 0.0")]
    [InlineData("-total > 0.0 ? total : 0.0")]
    [InlineData("(total > 5.0) == is_public ? total : 0.0")]
    public void A_computed_comparison_over_a_non_leaf_operand_is_refused_at_compile_time(string source)
    {
        var result = CelFixtures.Compiler.Compile(source, CelProfile.Computed, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("field, a literal or a context value");
    }

    [Theory]
    [InlineData("(is_public ? total > 5.0 : false) ? total : 0.0")]
    [InlineData("!(is_public ? has(title) : is_public) ? total : 0.0")]
    public void A_computed_ternary_branch_that_is_a_predicate_is_refused_at_compile_time(string source)
    {
        var result = CelFixtures.Compiler.Compile(source, CelProfile.Computed, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Message.ShouldContain("ternary branch");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("&&");
    }

    /// <summary>
    /// The in-memory profile keeps every one of these shapes: <see cref="CelProfile.Condition"/> is never
    /// rendered to SQL, so narrowing it would refuse a working hook for a backend it never meets.
    /// </summary>
    [Theory]
    [InlineData("(total > 5.0) == true")]
    [InlineData("has(title) == is_public")]
    [InlineData("!(is_public) == is_public")]
    public void The_interpreter_only_condition_profile_still_accepts_a_compared_predicate(string source)
    {
        var compiled = CelFixtures.CompileCondition(source);

        compiled.ResultType.ShouldBe(CelValueType.Bool);
    }

    [Theory]
    [InlineData("(total > 5.0) == true", 10.0, true)]
    [InlineData("(total > 5.0) == true", 1.0, false)]
    [InlineData("(total > 5.0) == false", 1.0, true)]
    public void The_interpreter_evaluates_a_compared_predicate_in_the_condition_profile(
        string source, double total, bool expected)
    {
        var compiled = CelFixtures.CompileCondition(source);

        CelFixtures.Evaluator.Evaluate(compiled, CelFixtures.Row(("total", (decimal)total)), null, CelFixtures.Alice)
            .ShouldBe(expected);
    }

    /// <summary>
    /// The shapes the refusal leaves in place are the ones the SQL backend has always rendered — pinned so
    /// the narrowing cannot overshoot into refusing a leaf comparison.
    /// </summary>
    [Theory]
    [InlineData("is_public == true")]
    [InlineData("owner_id == @user.id")]
    [InlineData("total > 5.0 && !(title == 'x')")]
    public void A_leaf_comparison_rule_still_compiles_and_renders(string source)
    {
        var compiled = CelFixtures.CompileRule(source);

        Should.NotThrow(() => _renderer.Render(compiled, CelFixtures.Alice, _fields));
    }

    [Theory]
    [InlineData("total > 5.0 ? total : 0.0")]
    [InlineData("(is_public ? is_public : false) ? total : 0.0")]
    public void A_leaf_computed_ternary_still_compiles_and_renders(string source)
    {
        var compiled = CelFixtures.CompileComputed(source);

        Should.NotThrow(() => _renderer.Render(compiled, _fields));
    }
}
