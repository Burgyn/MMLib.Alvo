using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Judges the relation terms of a predicate over a row that is not stored yet: each one is one
/// <c>SELECT CASE WHEN EXISTS (…) THEN 1 ELSE 0 END</c>, its outer values bound from the post-image, and its verdict is
/// substituted into the tree so the interpreter judges the rest.
/// </summary>
/// <remarks>
/// The probe runs through EF's own <c>SqlQueryRaw</c> on the write's context, so it joins whatever transaction that
/// context holds. It takes no lock: a concurrent transaction may change the related row between the probe and the
/// commit, the gap a PostgreSQL RLS sub-select has too.
/// </remarks>
/// <param name="predicates">The policy predicate renderer.</param>
/// <param name="fields">The engine's field rendering.</param>
/// <param name="dialect">The engine's dialect, for each related table's unlocked source.</param>
internal sealed class RelationProbe(IPredicateRenderer predicates, IFieldSqlRenderer fields, IAlvoSqlDialect dialect)
{
    /// <summary>
    /// The expression with every top-level relation replaced by its verdict, or the expression itself when it reads none.
    /// </summary>
    /// <param name="db">The write's context.</param>
    /// <param name="expression">A bound rule.</param>
    /// <param name="outer">The entity the post-image belongs to.</param>
    /// <param name="context">The caller.</param>
    /// <param name="image">The post-image.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    internal async Task<CompiledExpression> ResolvedAsync(
        AlvoDataContext db, CompiledExpression expression, EntitySchema outer, AlvoContext context, AlvoRecord image,
        CancellationToken cancellationToken)
    {
        if (!CelRewriter.HasRelation(expression.Root))
        {
            return expression;
        }

        var verdicts = new Dictionary<CelRelation, bool>(ReferenceEqualityComparer.Instance);
        foreach (var relation in Relations(expression.Root))
        {
            verdicts[relation] = await HoldsAsync(db, relation, outer, context, image, cancellationToken).ConfigureAwait(false);
        }

        var root = CelRewriter.Replace(expression.Root, node =>
            node is CelRelation relation ? new CelLiteral(CelValueType.Bool, verdicts[relation]) : null);
        return new CompiledExpression(root, expression.Profile, expression.ResultType, expression.Source, expression.Entity);
    }

    /// <summary>One relation term over the post-image: whether a related row the caller may list holds.</summary>
    /// <param name="db">The write's context.</param>
    /// <param name="relation">A bound relation.</param>
    /// <param name="outer">The entity the post-image belongs to.</param>
    /// <param name="context">The caller.</param>
    /// <param name="image">The post-image.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    internal async Task<bool> HoldsAsync(
        AlvoDataContext db, CelRelation relation, EntitySchema outer, AlvoContext context, AlvoRecord image,
        CancellationToken cancellationToken)
    {
        var source = new RelationSql(entity => dialect.RenderTable(entity, lockedPreImageFor: null), new RowSource.Image(image));
        var exists = predicates.RenderRelation(relation, outer, context, fields, PolicyParameterPrefix.Probe, source);
        var parameters = new PredicateParameterBinder(db).Bind(
            db.Rows(outer.Name).EntityType,
            exists.Parameters.ToDictionary(pair => pair.Key, pair => BoundValue.FromPolicyPredicate(pair.Value), StringComparer.Ordinal));

        var verdict = await db.Database.SqlQueryRaw<int>(Statement(exists.Sql), parameters).ToListAsync(cancellationToken).ConfigureAwait(false);
        return verdict.Single() == 1;
    }

    private static string Statement(string exists) => $"SELECT CASE WHEN {exists} THEN 1 ELSE 0 END";

    /// <summary>The relation terms not nested in another: a nested one is part of its parent's <c>EXISTS</c>.</summary>
    private static List<CelRelation> Relations(CelNode root)
    {
        var found = new List<CelRelation>();
        CelRewriter.Replace(root, node =>
        {
            if (node is CelRelation relation)
            {
                found.Add(relation);
                return node;
            }

            return null;
        });
        return found;
    }
}
