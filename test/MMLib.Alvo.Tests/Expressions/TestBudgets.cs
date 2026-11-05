using Microsoft.Extensions.Options;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A budget policy for facts that build a runner or an evaluator by hand.</summary>
internal static class TestBudgets
{
    internal static CelFunctionBudgetPolicy Default(TimeProvider? time = null, TimeSpan? limit = null) =>
        new(Options.Create(new AlvoExtensionOptions { CelFunctionBudget = limit ?? TimeSpan.FromMilliseconds(250) }), time ?? TimeProvider.System);
}
