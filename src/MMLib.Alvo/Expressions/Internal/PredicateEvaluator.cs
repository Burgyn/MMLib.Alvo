using MMLib.Alvo.Data;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// The default <see cref="IPredicateEvaluator"/>: a thin adapter over <see cref="CelInterpreter"/>,
/// registered so no consumer needs assembly-level access to the core's internal interpreter to
/// evaluate a policy predicate against a row.
/// </summary>
internal sealed class PredicateEvaluator : IPredicateEvaluator
{
    /// <inheritdoc/>
    public bool Evaluate(CompiledExpression expression, AlvoRecord current, AlvoRecord? previous, AlvoContext context) =>
        CelInterpreter.EvaluatePredicate(expression, current, previous, context);
}
