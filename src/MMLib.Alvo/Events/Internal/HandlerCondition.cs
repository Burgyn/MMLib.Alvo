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

/// <summary>A verdict, and what explains it: the caller value the envelope lacks, or the type of what the condition threw.</summary>
/// <param name="Verdict">What the condition says.</param>
/// <param name="Unread">The reference the envelope does not carry, when that is why nothing matched.</param>
/// <param name="ErrorType">The type name of what the condition threw, when it failed — never its message.</param>
internal readonly record struct HandlerJudgement(HandlerVerdict Verdict, string? Unread = null, string? ErrorType = null)
{
    internal static HandlerJudgement Match { get; } = new(HandlerVerdict.Match);

    internal static HandlerJudgement NoMatch { get; } = new(HandlerVerdict.NoMatch);
}

/// <summary>
/// Judges a handler's condition at fan-out, after the commit and before any delivery exists (spec X6). A condition reading
/// a caller value the envelope lacks matches nothing, as an after-hook's does; one that throws is an outcome — the
/// fan-out records it dead — never a failure of the batch (R-M6).
/// </summary>
internal static class HandlerCondition
{
    private const string UserReference = "@user.id";
    private const string TenantReference = "@tenant.id";
    private const string RolesReference = "@user.roles";

    internal static HandlerJudgement Judge(BoundCondition condition, AlvoEvent @event, IPredicateEvaluator evaluator, EnvelopeCaller caller)
    {
        if (condition.Expression is null)
        {
            return HandlerJudgement.Match;
        }

        if (Unread(condition, caller) is { } reference)
        {
            return HandlerJudgement.NoMatch with { Unread = reference };
        }

        try
        {
            return EnvelopeReading.Holds(condition.Expression, @event.Data, evaluator, caller.Context) ? HandlerJudgement.Match : HandlerJudgement.NoMatch;
        }
        catch (Exception failure)
        {
            return new HandlerJudgement(HandlerVerdict.Failed, ErrorType: failure.GetType().Name);
        }
    }

    /// <summary>The caller value <paramref name="condition"/> reads that the envelope does not carry, if any — the after-hooks' gate.</summary>
    private static string? Unread(BoundCondition condition, EnvelopeCaller caller) =>
        condition.Required.UserIdMissingFrom(caller.Context) ? UserReference
        : condition.Required.TenantIdMissingFrom(caller.Context) ? TenantReference
        : condition.ReadsRoles && !caller.CarriesRoles ? RolesReference
        : null;
}
