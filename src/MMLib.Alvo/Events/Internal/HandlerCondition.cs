using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Events.Internal;

/// <summary>What one handler's <c>when</c> says about one event.</summary>
internal enum HandlerVerdict
{
    Match,
    NoMatch,
    Failed,
}

/// <summary>
/// Judges a handler's condition at fan-out, after the commit and before any delivery exists (spec X6). A condition reading
/// a caller value the envelope lacks matches nothing, as an after-hook's does; one that throws is an outcome — the
/// fan-out records it dead — never a failure of the batch (R-M6).
/// </summary>
internal static class HandlerCondition
{
    internal static HandlerVerdict Judge(BoundCondition condition, AlvoEvent @event, IPredicateEvaluator evaluator, EnvelopeCaller caller)
    {
        if (condition.Expression is null)
        {
            return HandlerVerdict.Match;
        }

        if (condition.Required.IsMissingFrom(caller.Context) || (condition.ReadsRoles && !caller.CarriesRoles))
        {
            return HandlerVerdict.NoMatch;
        }

        try
        {
            return EnvelopeReading.Holds(condition.Expression, @event.Data, evaluator, caller.Context) ? HandlerVerdict.Match : HandlerVerdict.NoMatch;
        }
        catch (Exception)
        {
            return HandlerVerdict.Failed;
        }
    }
}
