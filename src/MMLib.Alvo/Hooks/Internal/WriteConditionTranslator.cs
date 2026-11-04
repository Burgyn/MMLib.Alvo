using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using System.Linq.Expressions;
using System.Reflection;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// Translates a typed write hook's <c>when</c> by rewriting it into an <see cref="EventCondition{T}"/> lambda — <c>w.New</c> to
/// <c>e.New</c>, <c>w.Old</c>, <c>w.Caller</c>, <c>w.Changed(…)</c> likewise — and handing it to the one translator (Ruling 25).
/// The <c>Updated</c> verb admits both images; a create's absent <c>old.</c> compares false at run time.
/// </summary>
internal static class WriteConditionTranslator
{
    internal static string Translate<T>(Expression<Func<WriteCondition<T>, bool>> when, string id)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(when);
        var condition = Expression.Parameter(typeof(EventCondition<T>), "e");
        var changed = typeof(EventCondition<T>).GetMethod(nameof(EventCondition<T>.Changed))!;
        var body = new Rebinder(when.Parameters[0], condition, changed).Visit(when.Body);

        return ConditionTranslator.Translate(Expression.Lambda<Func<EventCondition<T>, bool>>(body, condition), id, HandlerVerb.Updated);
    }

    private sealed class Rebinder(ParameterExpression from, ParameterExpression to, MethodInfo changed) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node) =>
            node.Expression == from ? Expression.Property(to, node.Member.Name) : base.VisitMember(node);

        protected override Expression VisitMethodCall(MethodCallExpression node) =>
            node.Object == from && node.Method.Name == changed.Name
                ? Expression.Call(to, changed, node.Arguments.Select(Visit)!)
                : base.VisitMethodCall(node);
    }
}
