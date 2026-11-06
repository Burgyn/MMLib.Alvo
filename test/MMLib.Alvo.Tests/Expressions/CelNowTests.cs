using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary><c>now()</c> reads the flow's one instant in every row profile, and never a clock (HX-11 Rulings 1, 3, 5).</summary>
public sealed class CelNowTests
{
    private static readonly DateTimeOffset _t = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CelProfile.Rule)]
    [InlineData(CelProfile.Condition)]
    [InlineData(CelProfile.Mutate)]
    public void Now_compiles_in_every_row_profile(CelProfile profile) =>
        CelFixtures.Compiler.Compile(profile == CelProfile.Mutate ? "now()" : "created_at <= now()", profile, CelFixtures.Orders)
            .IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(CelProfile.Computed)]
    [InlineData(CelProfile.Access)]
    public void Now_is_refused_where_there_is_no_instant_to_bind(CelProfile profile) =>
        CelFixtures.Compiler.Compile("now()", profile, CelFixtures.Orders).IsSuccess.ShouldBeFalse();

    [Fact]
    public void A_computed_column_s_refusal_names_why_it_has_no_instant() =>
        CelFixtures.Compiler.Compile("now()", CelProfile.Computed, CelFixtures.Orders).Errors
            .ShouldContain(error => error.Message.Contains("maintained by the database and has no instant to read", StringComparison.Ordinal));

    [Fact]
    public void A_rule_reads_the_bound_instant()
    {
        var rule = CelFixtures.CompileRule("created_at <= now()");
        var row = CelFixtures.Row(("created_at", _t));
        using (AlvoFlow.EnterInstant(_t))
        {
            CelFixtures.Evaluator.Evaluate(rule, row, null, CelFixtures.Alice).ShouldBeTrue();
        }

        using (AlvoFlow.EnterInstant(_t.AddSeconds(-1)))
        {
            CelFixtures.Evaluator.Evaluate(rule, row, null, CelFixtures.Alice).ShouldBeFalse();
        }
    }

    [Fact]
    public void A_condition_reads_the_bound_instant()
    {
        var condition = CelFixtures.CompileCondition("new.created_at == now()");
        using var bound = AlvoFlow.EnterInstant(_t);

        CelInterpreter.EvaluatePredicate(condition, CelFixtures.Row(("created_at", _t)), null, CelFixtures.Alice).ShouldBeTrue();
    }

    [Fact]
    public void Now_with_no_bound_instant_fails_closed_and_reads_no_clock()
    {
        var condition = CelFixtures.CompileCondition("new.created_at <= now()");
        Should.Throw<CelFunctionException>(() =>
            CelInterpreter.EvaluatePredicate(condition, CelFixtures.Row(("created_at", _t)), null, CelFixtures.Alice))
            .OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_rule_s_now_with_no_bound_instant_is_a_defect_not_a_verdict()
    {
        var rule = CelFixtures.CompileRule("created_at <= now()");
        Should.Throw<CelFunctionException>(() =>
            CelInterpreter.EvaluatePredicate(rule, CelFixtures.Row(("created_at", _t)), null, CelFixtures.Alice))
            .OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_rule_renders_now_as_the_bound_instant()
    {
        var rule = CelFixtures.CompileRule("created_at <= now()");
        using var bound = AlvoFlow.EnterInstant(_t.AddTicks(3));

        var predicate = new SqlPredicateRenderer().Render(rule, CelFixtures.Alice, new TestFieldSqlRenderer());

        predicate.Parameters.Values.ShouldBe([_t]);
    }

    [Fact]
    public void A_rule_with_now_refuses_to_render_with_no_bound_instant() =>
        Should.Throw<InvalidOperationException>(() =>
            new SqlPredicateRenderer().Render(CelFixtures.CompileRule("created_at <= now()"), CelFixtures.Alice, new TestFieldSqlRenderer()))
            .Message.ShouldContain("no instant bound");

    [Fact]
    public void A_mutate_s_explicit_instant_wins_over_the_flow_s()
    {
        var mutate = CelFixtures.CompileMutate("now()");
        using var bound = AlvoFlow.EnterInstant(_t.AddDays(1));
        CelInterpreter.EvaluateMutation(mutate, CelFixtures.Row(), null, _t).ShouldBe(_t);
    }
}
