using MMLib.Alvo.Data;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// The default <see cref="IPredicateEvaluator"/>: a thin adapter over <see cref="CelInterpreter"/>,
/// registered so no consumer needs assembly-level access to the core's internal interpreter to
/// evaluate a policy predicate against a row. A <c>Condition</c> expression — the only profile besides <c>Mutate</c> that
/// may call a host function — is evaluated in a host-function budget pass; a <c>Rule</c> or <c>Access</c> expression opens
/// none, so the rule path pays nothing.
/// </summary>
/// <param name="budget">Opens the pass; <see langword="null"/> in a hand-built evaluator, which then runs host functions unbudgeted.</param>
internal sealed class PredicateEvaluator(CelFunctionBudgetPolicy? budget = null) : IPredicateEvaluator, IChangeListPredicateEvaluator
{
    /// <inheritdoc/>
    public bool Evaluate(CompiledExpression expression, AlvoRecord current, AlvoRecord? previous, AlvoContext context)
    {
        using var pass = PassFor(expression);
        return CelInterpreter.EvaluatePredicate(expression, current, previous, context);
    }

    /// <inheritdoc/>
    public bool Evaluate(
        CompiledExpression expression,
        AlvoRecord current,
        AlvoRecord? previous,
        IReadOnlyCollection<string> changed,
        AlvoContext context)
    {
        using var pass = PassFor(expression);
        return CelInterpreter.EvaluateCondition(expression, current, previous, context, changed);
    }

    private CelFunctionBudget? PassFor(CompiledExpression expression) =>
        budget is not null && expression?.Profile == CelProfile.Condition ? budget.Enter() : null;
}
