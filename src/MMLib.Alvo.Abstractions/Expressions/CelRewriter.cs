namespace MMLib.Alvo.Expressions;

/// <summary>
/// The one tree rewrite and the one tree search over a relation-bearing tree, shared by the lifter, the binder and the
/// driver's probe substitution, so a node kind is taught to all of them at once.
/// </summary>
internal static class CelRewriter
{
    /// <summary>
    /// Rebuilds <paramref name="node"/> bottom-up wherever <paramref name="replace"/> returns a node; a replaced node is
    /// not descended into.
    /// </summary>
    /// <param name="node">The tree to rewrite.</param>
    /// <param name="replace">The replacement for a node, or <see langword="null"/> to keep it and descend.</param>
    internal static CelNode Replace(CelNode node, Func<CelNode, CelNode?> replace) => replace(node) ?? node switch
    {
        CelUnary unary => unary with { Operand = Replace(unary.Operand, replace) },
        CelBinary binary => binary with { Left = Replace(binary.Left, replace), Right = Replace(binary.Right, replace) },
        CelConditional c => c with
        {
            Condition = Replace(c.Condition, replace),
            WhenTrue = Replace(c.WhenTrue, replace),
            WhenFalse = Replace(c.WhenFalse, replace),
        },
        CelRelation relation => relation with { Body = Replace(relation.Body, replace) },
        CelHas has => Presence(has, Replace(has.Field, replace)),
        _ => node.RebuildChildren(child => Replace(child, replace)),
    };

    /// <summary>Whether <paramref name="match"/> holds for <paramref name="node"/> or any node below it.</summary>
    /// <param name="node">The tree to search.</param>
    /// <param name="match">The test.</param>
    internal static bool Any(CelNode node, Func<CelNode, bool> match) => match(node) || node switch
    {
        CelUnary unary => Any(unary.Operand, match),
        CelBinary binary => Any(binary.Left, match) || Any(binary.Right, match),
        CelConditional c => Any(c.Condition, match) || Any(c.WhenTrue, match) || Any(c.WhenFalse, match),
        CelRelation relation => Any(relation.Body, match),
        CelHas has => Any(has.Field, match),
        CelOuterHas outerHas => Any(outerHas.Field, match),
        _ => node.ChildNodes.Any(child => Any(child, match)),
    };

    /// <summary>Whether the tree holds a relation term anywhere.</summary>
    /// <param name="node">The tree to search.</param>
    internal static bool HasRelation(CelNode node) => Any(node, static n => n is CelRelation);

    /// <summary>
    /// <c>has(field)</c> holds a <see cref="CelFieldRef"/> by type, so a field rewritten to the outer row becomes
    /// <see cref="CelOuterHas"/>; otherwise <c>has(outerField)</c> would read the related row.
    /// </summary>
    private static CelNode Presence(CelHas has, CelNode field) => field switch
    {
        CelFieldRef same => has with { Field = same },
        CelOuterFieldRef outer => new CelOuterHas(outer),
        _ => throw new InvalidOperationException("has() takes a field; a rewrite turned its field into another node."),
    };
}
