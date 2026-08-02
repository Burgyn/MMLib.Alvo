using MMLib.Alvo.Data;

namespace MMLib.Alvo.Expressions;

/// <summary>
/// Evaluates a compiled CEL expression against a row in memory — the row-level counterpart to
/// <see cref="IPredicateRenderer"/>'s SQL rendering. Any component that must apply a policy
/// predicate (<c>USING</c>, <c>WITH CHECK</c>, a synthesized tenant scope) to an in-memory row —
/// an in-memory data port, an in-transaction before-hook condition, or a dynamic-entity driver
/// with no SQL backend to push the predicate into — depends on this port rather than on the
/// core's internal interpreter directly, so the trust boundary between "the one real CEL
/// evaluator" and everything that consumes it is a published contract, not an assembly-visibility
/// workaround.
/// </summary>
public interface IPredicateEvaluator
{
    /// <summary>
    /// Evaluates a Rule or Condition expression's boolean verdict over a candidate row. The
    /// semantics — the null rule, short-circuiting, <c>changed(...)</c>, numeric widening — are
    /// exactly those the core's CEL interpreter documents, and the differential suite proves this
    /// evaluator and <see cref="IPredicateRenderer"/> never disagree on any well-typed expression
    /// and row.
    /// </summary>
    /// <param name="expression">The compiled Rule or Condition expression.</param>
    /// <param name="current">
    /// The row being evaluated — the complete post-image on a create/update, every persisted
    /// field, never a partial payload: a field the caller did not mention must read as unchanged,
    /// not as explicitly set to <see langword="null"/>.
    /// </param>
    /// <param name="previous">
    /// The row as it was before the change, or <see langword="null"/> on a create; only read by
    /// <c>old.</c> field references and <c>changed(...)</c>.
    /// </param>
    /// <param name="context">The caller/tenant context <c>@user</c>/<c>@tenant</c> resolve against.</param>
    bool Evaluate(CompiledExpression expression, AlvoRecord current, AlvoRecord? previous, AlvoContext context);
}
