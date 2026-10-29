using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using System.Linq.Expressions;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// Translates a typed access lambda by rewriting it into the shape of an event handler's <c>when</c> — the row becomes
/// <c>e.New</c>, the caller <c>e.Caller</c> — and handing it to the one condition translator (Ruling 2).
/// </summary>
/// <remarks>
/// Translating as a create's condition is what refuses <c>old.</c> for free: a command's row has no before-image. A refusal
/// quotes the rewritten node and names the <c>access</c> slot.
/// </remarks>
internal static class AccessTranslator
{
    private const string AccessFix = "write the access rule as a CEL string — access: \"…\" — or check the rest in the handler body after the rule admits the caller.";

    private const string SplitAccessFix = "split the access rule into a shorter one, or write it as a CEL string — access: \"…\" — or check the rest in the handler body after the rule admits the caller.";

    /// <summary>Translates a caller-only rule.</summary>
    /// <param name="access">The lambda.</param>
    /// <param name="name">The command's name.</param>
    internal static string Translate(Expression<Func<AlvoConditionCaller, bool>> access, string name)
    {
        ArgumentNullException.ThrowIfNull(access);
        var condition = Expression.Parameter(typeof(EventCondition<AlvoRecord>), "e");
        var body = Rebind(access.Body, access.Parameters[0], Expression.Property(condition, nameof(EventCondition<>.Caller)));

        return Translated(Expression.Lambda<Func<EventCondition<AlvoRecord>, bool>>(body, condition), name);
    }

    /// <summary>Translates a rule over the bound row and the caller.</summary>
    /// <typeparam name="TEntity">The typed record of the bound entity.</typeparam>
    /// <param name="access">The lambda.</param>
    /// <param name="name">The command's name.</param>
    internal static string Translate<TEntity>(Expression<Func<TEntity, AlvoConditionCaller, bool>> access, string name)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(access);
        var condition = Expression.Parameter(typeof(EventCondition<TEntity>), "e");
        var row = Rebind(access.Body, access.Parameters[0], Expression.Property(condition, nameof(EventCondition<>.New)));
        var body = Rebind(row, access.Parameters[1], Expression.Property(condition, nameof(EventCondition<>.Caller)));

        return Translated(Expression.Lambda<Func<EventCondition<TEntity>, bool>>(body, condition), name);
    }

    private static string Translated<T>(Expression<Func<EventCondition<T>, bool>> when, string name)
        where T : class
    {
        try
        {
            return ConditionTranslator.Translate(when, name, HandlerVerb.Created);
        }
        catch (AlvoTranslationException refusal)
        {
            throw AsAccess(refusal);
        }
    }

    /// <summary>The <c>when</c> refusal restated for the <c>access</c> slot: its slot and every fix that names <c>when:</c>.</summary>
    private static AlvoTranslationException AsAccess(AlvoTranslationException refusal)
    {
        var fix = refusal.FixSuggestion switch
        {
            TranslationRefusal.SplitWhenFix => SplitAccessFix,
            var named when named.Contains("when:", StringComparison.Ordinal) => AccessFix,
            var specific => specific,
        };
        var message = refusal.Message.Replace(" (when): ", " (access): ", StringComparison.Ordinal);
        message = message.EndsWith(refusal.FixSuggestion, StringComparison.Ordinal) ? message[..^refusal.FixSuggestion.Length] + fix : message;

        return new AlvoTranslationException(message, fix);
    }

    private static Expression Rebind(Expression body, ParameterExpression from, Expression to) => new Rebinder(from, to).Visit(body);

    private sealed class Rebinder(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
