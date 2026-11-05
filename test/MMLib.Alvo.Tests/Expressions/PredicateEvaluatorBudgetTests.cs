using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>After-hook and handler conditions are evaluated post-commit by the evaluator, which opens a pass for them too.</summary>
public sealed class PredicateEvaluatorBudgetTests
{
    private static readonly AlvoContext _caller = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    [Fact]
    public void A_condition_s_host_function_runs_under_the_budget()
    {
        var clock = new FakeTimeProvider();
        var slow = HostCelFunction.Create("slow", (string s) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(251));
            return s;
        }, summary: null);
        var condition = TestCelFunctions.Compile("slow('x') == 'x'", CelProfile.Condition, slow);

        Should.Throw<CelFunctionException>(
            () => new PredicateEvaluator(TestBudgets.Default(clock)).Evaluate(condition, AlvoRecord.Empty, null, _caller))
            .Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_condition_inside_its_budget_answers()
    {
        var slow = HostCelFunction.Create("slow", (string s) => s, summary: null);
        var condition = TestCelFunctions.Compile("slow('x') == 'x'", CelProfile.Condition, slow);

        new PredicateEvaluator(TestBudgets.Default(new FakeTimeProvider())).Evaluate(condition, AlvoRecord.Empty, null, _caller).ShouldBeTrue();
    }
}
