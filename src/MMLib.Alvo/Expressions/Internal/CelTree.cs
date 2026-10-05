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
        CelCall call => call.Arguments,
        _ => throw new InvalidOperationException(
            $"'{node.GetType().Name}' is not a known CEL node kind; its subtree cannot be walked."),
    };
}

/// <summary>
/// A function call: one of the two legacy calls with their own grammar (<c>lowerAscii(field)</c>, <c>now()</c>) or a
/// function from the <c>CelFunctionCatalog</c>. Legal only where the type checker's profile gates allow it, and never
/// rendered to SQL in this slice.
/// </summary>
/// <remarks>
/// <para>
/// The node is deliberately <see langword="internal"/> while the rest of the <see cref="CelNode"/>
/// hierarchy is public: the allow-list is closed at two entries, so nothing outside the core has a
/// reason to pattern-match this kind, and keeping it internal means the published AST does not grow a
/// case every out-of-repo walker would have to learn. It still derives from the public
/// <see cref="CelNode"/>, so <c>CompiledExpression.Root</c> can carry one; an external walker sees an
/// unrecognized node rather than a node it can mis-handle.
/// </para>
/// <para>
/// <b>The call shape is the deviation, the name and semantics are the standard's.</b> Conformant CEL
/// spells the fold <c>x.lowerAscii()</c>, a receiver-style macro Alvo's grammar cannot express (one
/// level of <c>old.</c>/<c>new.</c> qualification is all a field path may carry, so
/// <c>new.email.lowerAscii()</c> is structurally impossible). Alvo therefore adopts the standard's
/// name and its ASCII-only semantics and deviates only on the call shape, exactly as
/// <c>has(...)</c>/<c>changed(...)</c> already do. <c>lower(...)</c> is refused with a fix suggestion
/// naming <see cref="LowerAscii"/>.
/// </para>
/// </remarks>
/// <param name="Name">The function's CEL spelling.</param>
/// <param name="Arguments">Every argument, in source order; empty for a nullary call such as <see cref="Now"/>.</param>
internal sealed record CelCall(string Name, IReadOnlyList<CelNode> Arguments) : CelNode
{
    /// <summary>The ASCII-only lower-case fold, <c>lowerAscii(field)</c>: folds <c>A</c>–<c>Z</c> and nothing else.</summary>
    public const string LowerAscii = "lowerAscii";

    /// <summary>
    /// The write's own instant, <c>now()</c> — not a clock read. It resolves to the
    /// <see cref="DateTimeOffset"/> the caller bound for the whole write (the same one the audit stamp
    /// uses), so two evaluations inside one write can never disagree.
    /// </summary>
    public const string Now = "now";

    /// <summary>
    /// The call's result type as the type checker resolved it; <see cref="CelValueType.Null"/> on a tree straight
    /// from the parser, which knows names but not types.
    /// </summary>
    public CelValueType ResultType { get; init; } = CelValueType.Null;
}
