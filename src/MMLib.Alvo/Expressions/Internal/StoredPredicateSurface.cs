namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// What a stored predicate — a <c>field.validation</c> written as a <c>CHECK</c>, a <c>rollup.where</c> — may read: the row
/// alone (HX-11 Rulings 13, 31). A Rule-profile expression admits the caller, the tenant, a role test and <c>now()</c>; a stored
/// predicate is judged by the database on every write, with no caller and no instant, so each of those is refused here.
/// </summary>
/// <remarks>
/// A folded <c>duration('…')</c> is a literal by the time a tree is compiled, so it never appears here; any call that
/// survives type checking does, and is refused. Positions are 0: the compiled tree carries none, and the caller attaches
/// the descriptor pointer.
/// </remarks>
internal static class StoredPredicateSurface
{
    private const string RowFix = "Compare two of the row's fields, or a field with a constant.";
    private const string NowFix = "Store the instant in a field with a before-hook mutate (now()) and compare that field.";
    private const string CallFix = "Store the function's result in a field with a before-hook mutate, and compare that field.";

    /// <summary>One refusal per construct a stored predicate cannot carry, each opening with <paramref name="slot"/>.</summary>
    /// <param name="expression">A compiled Rule-profile expression.</param>
    /// <param name="slot">The place the predicate is stored, as a sentence opens: <c>"A validation"</c>, <c>"A rollup filter"</c>.</param>
    /// <returns>The refusals, empty when the expression reads the row alone.</returns>
    public static IReadOnlyList<CelCompilationError> Refusals(CompiledExpression expression, string slot)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);

        var refusals = new List<CelCompilationError>();
        Collect(expression.Root, slot, refusals);
        return refusals;
    }

    /// <summary>
    /// Walks the tree, refusing a node and not its subtree: a role test is one refusal naming the role, not a second for the
    /// <c>@user.roles</c> it reads.
    /// </summary>
    private static void Collect(CelNode node, string slot, List<CelCompilationError> refusals)
    {
        if (Refusal(node, slot) is { } refusal)
        {
            refusals.Add(refusal);
            return;
        }

        foreach (var child in CelTree.Children(node))
        {
            Collect(child, slot, refusals);
        }
    }

    private static CelCompilationError? Refusal(CelNode node, string slot) => node switch
    {
        CelBinary { Operator: CelBinaryOperator.In } => new(
            $"{slot} cannot test the caller's role — a stored constraint has no caller.", RowFix, 0),
        CelContextRef contextRef => new(
            $"{slot} reads the row alone, and {Spelling(contextRef.Value)} is the caller's — a stored constraint has no caller.", RowFix, 0),
        CelCall { Name: CelCall.Now } => new(
            $"{slot} is stored and judged on every write, so now() would change its answer with no write — refused.", NowFix, 0),
        CelCall call => new($"{slot} cannot call a function ({call.Name}).", CallFix, 0),
        _ => null,
    };

    private static string Spelling(CelContextValue value) => value switch
    {
        CelContextValue.UserId => "@user.id",
        CelContextValue.UserRoles => "@user.roles",
        CelContextValue.TenantId => "@tenant.id",
        _ => $"@{value}",
    };
}
