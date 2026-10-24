using System.Linq.Expressions;
using System.Reflection;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// Values in a lambda that do not read its parameter: strictly, for a condition translated once (spec 2026-10-14 §4.3:
/// only a constant, a <c>static readonly</c> field or an enum member), and per call, for a query (any value).
/// </summary>
internal static class LambdaValues
{
    internal static bool TryConstant(Expression node, out object? value, out string reason)
    {
        (value, reason) = (null, string.Empty);
        switch (node)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;
            case MemberExpression { Expression: null, Member: FieldInfo field } when field.IsInitOnly || field.IsLiteral:
                value = field.GetValue(null);
                return true;
            case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array:
                return TryArray(array, out value, out reason);
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Negate or ExpressionType.NegateChecked } unary:
                return TryFolded(unary, out value, out reason);
            default:
                reason = Why(node);
                return false;
        }
    }

    internal static object? Evaluate(Expression node) => node switch
    {
        ConstantExpression constant => constant.Value,
        MemberExpression { Member: FieldInfo field, Expression: null or ConstantExpression } member => field.GetValue((member.Expression as ConstantExpression)?.Value),
        _ => Expression.Lambda<Func<object?>>(Expression.Convert(node, typeof(object))).Compile(preferInterpretation: true)(),
    };

    private static bool TryFolded(UnaryExpression unary, out object? value, out string reason)
    {
        value = null;
        if (!TryConstant(unary.Operand, out _, out reason))
        {
            return false;
        }

        value = Evaluate(unary);
        return true;
    }

    private static bool TryArray(NewArrayExpression array, out object? value, out string reason)
    {
        var items = new object?[array.Expressions.Count];
        for (var index = 0; index < items.Length; index++)
        {
            if (!TryConstant(array.Expressions[index], out items[index], out reason))
            {
                value = null;
                return false;
            }
        }

        (value, reason) = (items, string.Empty);
        return true;
    }

    private static string Why(Expression node) => node switch
    {
        MemberExpression { Expression: ConstantExpression, Member.Name: var name } =>
            $"'{name}' is a captured variable, but a condition is translated once, when the handler is registered, so it may hold only constants, static readonly fields and enum members",
        MemberExpression { Expression: null, Member: FieldInfo field } => $"'{field.Name}' is a static field that can change after the handler is registered",
        MemberExpression { Expression: null, Member: PropertyInfo property } => $"'{property.Name}' is a static property, computed again whenever it is read",
        _ => "it is computed when the condition runs, not a constant",
    };
}
