using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A budget policy for facts that build a runner or an evaluator by hand.</summary>
internal static class TestBudgets
{
    /// <summary>
    /// The default 250 ms budget, on <paramref name="time"/> — or, when a fact passes none, on a clock that never moves, so
    /// a function that returns at once cannot cross the budget on a loaded runner. A fact about the budget passes its own clock.
    /// </summary>
    internal static CelFunctionBudgetPolicy Default(TimeProvider? time = null, TimeSpan? limit = null) =>
        new(Options.Create(new AlvoExtensionOptions { CelFunctionBudget = limit ?? TimeSpan.FromMilliseconds(250) }), time ?? new FakeTimeProvider());
}
