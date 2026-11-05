using Microsoft.Extensions.Options;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>Opens host-function passes with the configured budget on the host's clock.</summary>
/// <param name="options">The extension options (<c>Alvo:Extensions:CelFunctionBudget</c>).</param>
/// <param name="time">The host's clock.</param>
internal sealed class CelFunctionBudgetPolicy(IOptions<AlvoExtensionOptions> options, TimeProvider time)
{
    /// <summary>Opens a pass; dispose it when the evaluation ends.</summary>
    /// <param name="outer">The caller's token, if the evaluation has one.</param>
    /// <returns>The pass.</returns>
    internal CelFunctionBudget Enter(CancellationToken outer = default) =>
        CelFunctionBudget.Enter(options.Value.CelFunctionBudget, time, outer);
}
