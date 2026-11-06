using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Turns every forward leaf of a type-checked rule into one <see cref="CelRelation"/>, and refuses what would let a
/// relation widen access: a relation under <c>!</c>, and one comparison reading two relations.
/// </summary>
/// <remarks>
/// Only <c>&amp;&amp;</c> and <c>||</c> are descended: a relation that becomes invisible to the caller then only ever
/// turns its clause false, so a rule only narrows. Any other node holding a forward path is a leaf, lifted whole, so
/// its outer fields become <see cref="CelOuterFieldRef"/> and its paths the related row's fields.
/// </remarks>
internal static class RelationLifter
{
    internal const string NegatedMessage =
        "A relation is negated here. A rule may only narrow as related rows become invisible, so a relation cannot sit under '!'.";

    internal const string NegatedFix = "Say what must hold instead, or count the rows with a rollup and compare the count.";

    internal const string TwoRelationsFix = "Compare each relation with a value or the caller, in its own clause.";

    /// <summary>The lifted tree; every refusal is appended to <paramref name="errors"/>.</summary>
    /// <param name="node">A type-checked rule tree.</param>
    /// <param name="errors">The compile's error list.</param>
    internal static CelNode Lift(CelNode node, List<CelCompilationError> errors)
    {
        var lifted = LiftNode(node, errors);
        if (errors.Count == 0 && CelRewriter.Any(lifted, static n => n is CelHop))
        {
            errors.Add(new CelCompilationError("A relation path was left outside a relation term.", null, 0));
        }

        return lifted;
    }

    private static CelNode LiftNode(CelNode node, List<CelCompilationError> errors) => node switch
    {
        CelBinary { Operator: CelBinaryOperator.And or CelBinaryOperator.Or } b =>
            b with { Left = LiftNode(b.Left, errors), Right = LiftNode(b.Right, errors) },
        CelUnary { Operator: CelUnaryOperator.Not } u when Reads(u.Operand) => Refuse(u, errors, NegatedMessage, NegatedFix),
        CelRelation r => r,
        _ when Hops(node) is { Count: 1 } hops => Forward(node, hops[0]),
        _ when Hops(node) is { Count: > 1 } hops => Refuse(node, errors, TwoRelations(hops), TwoRelationsFix),
        _ => node,
    };

    private static CelRelation Forward(CelNode leaf, CelHop hop) => new(
        CelRelationKind.Forward,
        hop.Related,
        AlvoManagedColumns.Id,
        hop.Via,
        hop.Via,
        CelRewriter.Replace(leaf, static n => n switch
        {
            CelHop h => h.Field,
            CelFieldRef outer => new CelOuterFieldRef(outer.FieldName, outer.Type),
            _ => null,
        }));

    /// <summary>The first hop through each distinct ref the leaf reads, in source order.</summary>
    private static List<CelHop> Hops(CelNode node)
    {
        var hops = new List<CelHop>();
        CelRewriter.Any(node, n =>
        {
            if (n is CelHop hop && !hops.Exists(known => known.Via == hop.Via))
            {
                hops.Add(hop);
            }

            return false;
        });
        return hops;
    }

    private static bool Reads(CelNode node) => CelRewriter.Any(node, static n => n is CelHop or CelRelation);

    private static string TwoRelations(List<CelHop> hops) =>
        $"One comparison reads two relations ('{hops[0].Via}', '{hops[1].Via}').";

    private static CelNode Refuse(CelNode node, List<CelCompilationError> errors, string message, string fix)
    {
        errors.Add(new CelCompilationError(message, fix, 0));
        return node;
    }
}
