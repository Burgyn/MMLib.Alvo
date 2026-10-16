using MMLib.Alvo.Data;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// An <see cref="IPredicateEvaluator"/> that can answer <c>changed(f)</c> from an event's <c>data.changed</c> list
/// rather than from a comparison of the two images — what an after-hook condition needs once
/// <c>events.exclude</c> has stripped a field from both images while the list still names it (spec E7).
/// </summary>
/// <remarks>
/// Internal on purpose: the published port keeps its four-argument shape, and only the core's own evaluator offers
/// this. The dispatcher prefers it whenever the registered evaluator implements it.
/// </remarks>
internal interface IChangeListPredicateEvaluator
{
    /// <summary>
    /// Evaluates a Condition expression's boolean verdict, answering <c>changed(f)</c> from
    /// <paramref name="changed"/>.
    /// </summary>
    /// <param name="expression">The compiled Condition expression.</param>
    /// <param name="current">The event's post-image.</param>
    /// <param name="previous">The event's pre-image, or <see langword="null"/> on a create.</param>
    /// <param name="changed">The fields the event names as moved.</param>
    /// <param name="context">The caller/tenant context <c>@user</c>/<c>@tenant</c> resolve against.</param>
    bool Evaluate(
        CompiledExpression expression,
        AlvoRecord current,
        AlvoRecord? previous,
        IReadOnlyCollection<string> changed,
        AlvoContext context);
}
