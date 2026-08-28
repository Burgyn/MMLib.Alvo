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
        CelCall call => call.Argument is null ? [] : [call.Argument],
        _ => throw new InvalidOperationException(
            $"'{node.GetType().Name}' is not a known CEL node kind; its subtree cannot be walked."),
    };
}

/// <summary>
/// A call to one of the two functions the <see cref="CelProfile.Mutate"/> profile allow-lists —
/// <c>lowerAscii(field)</c> and <c>now()</c>. Legal in no other profile, and never rendered to SQL.
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
/// <param name="Name">The function's CEL spelling — always <see cref="LowerAscii"/> or <see cref="Now"/>.</param>
/// <param name="Argument">
/// The single argument, or <see langword="null"/> for a nullary function (<see cref="Now"/>). The
/// arity is fixed per name by the parser, so a walker may switch on <see cref="Name"/> and trust it.
/// </param>
internal sealed record CelCall(string Name, CelNode? Argument) : CelNode
{
    /// <summary>The ASCII-only lower-case fold, <c>lowerAscii(field)</c>: folds <c>A</c>–<c>Z</c> and nothing else.</summary>
    public const string LowerAscii = "lowerAscii";

    /// <summary>
    /// The write's own instant, <c>now()</c> — not a clock read. It resolves to the
    /// <see cref="DateTimeOffset"/> the caller bound for the whole write (the same one the audit stamp
    /// uses), so two evaluations inside one write can never disagree.
    /// </summary>
    public const string Now = "now";
}
