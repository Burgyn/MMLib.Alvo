namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// The one place the <see cref="CelNode"/> hierarchy's shape is enumerated for walking. Every walker
/// that has to visit a whole tree — the compiler's depth cap, the catalog builder's role-literal
/// check — reads its children from here, so a new node kind is taught to all of them at once rather
/// than silently hiding its subtree from whichever walker was not updated.
/// </summary>
internal static class CelTree
{
    /// <summary>
    /// A node's direct children. Every known leaf kind is named explicitly, never matched by a
    /// wildcard, so a genuinely unrecognized node fails loudly instead of reporting an empty subtree;
    /// this can only be reached by a defect (a new <see cref="CelNode"/> case added without a case
    /// here), never by any source string a caller passes to <see cref="ICelCompiler.Compile"/>.
    /// </summary>
    /// <param name="node">The node whose children to enumerate.</param>
    /// <exception cref="InvalidOperationException"><paramref name="node"/> is not a known node kind.</exception>
    public static IReadOnlyList<CelNode> Children(CelNode node) => node switch
    {
        CelLiteral => [],
        CelFieldRef => [],
        CelContextRef => [],
        CelChanged => [],
        CelUnary unary => [unary.Operand],
        CelBinary binary => [binary.Left, binary.Right],
        CelConditional conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
        CelHas has => [has.Field],
        _ => throw new InvalidOperationException(
            $"'{node.GetType().Name}' is not a known CEL node kind; its subtree cannot be walked."),
    };
}
