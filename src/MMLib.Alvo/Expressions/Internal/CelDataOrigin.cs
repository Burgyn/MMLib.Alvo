namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Whose data a refusing node read (E1, Ruling 26): only the caller's when its subtree calls no host function and reads no
/// stored row. Decided on the failure path from the compiled tree, so a write that refuses nothing pays nothing.
/// </summary>
internal static class CelDataOrigin
{
    /// <summary>Whether <paramref name="node"/> reads only what the request carries.</summary>
    /// <param name="node">The node that saw the refusal.</param>
    /// <returns><see langword="false"/> when its subtree calls a host function, reads <c>old.</c> or asks <c>changed</c>.</returns>
    internal static bool IsCallersOwn(CelNode node) => node switch
    {
        CelCall { Function.IsHost: true } => false,
        CelFieldRef { State: CelRecordState.Old } => false,
        CelChanged => false,
        _ => CelTree.Children(node).All(IsCallersOwn),
    };

    /// <summary>Whether <paramref name="node"/> reads one of <paramref name="fields"/> from the row being written.</summary>
    /// <param name="node">The expression's root, or any node in it.</param>
    /// <param name="fields">The fields to look for.</param>
    /// <returns>Whether a <c>new.</c> (or unqualified) reference to one of them occurs.</returns>
    internal static bool Reads(CelNode node, IReadOnlySet<string> fields) => node switch
    {
        CelFieldRef { State: not CelRecordState.Old } field => fields.Contains(field.FieldName),
        _ => CelTree.Children(node).Any(child => Reads(child, fields)),
    };

    /// <summary>
    /// Runs <paramref name="evaluate"/>; a refusal of the caller's data whose expression reads a field a hook wrote earlier
    /// in this write is re-attributed as not the caller's. Conservative: the whole expression is judged, not the node.
    /// </summary>
    /// <typeparam name="T">What the evaluation answers.</typeparam>
    /// <param name="expression">The expression <paramref name="evaluate"/> evaluates.</param>
    /// <param name="writtenByHooks">The fields hooks wrote earlier in this write.</param>
    /// <param name="evaluate">The evaluation.</param>
    /// <returns>What <paramref name="evaluate"/> answered.</returns>
    internal static T Attributed<T>(CompiledExpression expression, IReadOnlySet<string> writtenByHooks, Func<T> evaluate)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(writtenByHooks);
        ArgumentNullException.ThrowIfNull(evaluate);
        try
        {
            return evaluate();
        }
        catch (CelFunctionException failure) when (failure.OnCallerData && writtenByHooks.Count > 0 && Reads(expression.Root, writtenByHooks))
        {
            throw failure.NotTheCallers();
        }
    }
}
