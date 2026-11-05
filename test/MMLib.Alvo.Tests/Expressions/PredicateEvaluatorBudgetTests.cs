using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// The evaluator the container builds carries the budget: nothing else pins it, and an evaluator built without the policy
    /// runs host functions unbudgeted with every hand-built fact still green.
    /// </summary>
    [Fact]
    public void The_container_built_evaluator_runs_a_condition_s_host_function_under_the_budget()
    {
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddAlvo(alvo => alvo.AddCelFunction("slow", (string s) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(251));
            return s;
        }));
        using var provider = services.BuildServiceProvider();
        var condition = provider.GetRequiredService<ICelCompiler>()
            .Compile("slow('x') == 'x'", CelProfile.Condition, TestCelFunctions.Items).Expression!;

        Should.Throw<CelFunctionException>(
            () => provider.GetRequiredService<IPredicateEvaluator>().Evaluate(condition, AlvoRecord.Empty, null, _caller))
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
