using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace MMLib.Alvo.Data.Internal;

/// <summary>What both translators ask of a lambda's tree: what reads the parameter, what reads a clock, what a conversion hides.</summary>
internal static class LambdaShape
{
    private static readonly HashSet<Type> _clockTypes = [typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly)];
    private static readonly HashSet<string> _clockMembers = ["Now", "UtcNow", "Today"];
    private static readonly HashSet<Type> _integral = [typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long)];
    private static readonly HashSet<Type> _small = [typeof(sbyte), typeof(byte), typeof(short), typeof(ushort)];
    private static readonly HashSet<Type> _collections = [typeof(List<>), typeof(HashSet<>), typeof(ICollection<>)];
    private static readonly HashSet<Type> _texts = [typeof(string), typeof(ReadOnlySpan<char>), typeof(Span<char>)];

    internal static bool Touches(Expression node, ParameterExpression parameter) => new ParameterFinder(parameter).Finds(node);

    internal static Expression? FindClock(Expression node) => new ClockFinder().Find(node);

    internal static bool IsClock(Expression node) => node switch
    {
        MemberExpression { Expression: null, Member: var member } => _clockTypes.Contains(member.DeclaringType!) && _clockMembers.Contains(member.Name),
        MethodCallExpression { Method: var method } => method.DeclaringType == typeof(TimeProvider) && method.Name is "GetUtcNow" or "GetLocalNow",
        _ => false,
    };

    internal static Expression Unwrapped(Expression node)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote } unary && IsTransparent(unary))
        {
            node = unary.Operand;
        }

        return node;
    }

    internal static Type? EnumBehind(Expression node) =>
        node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert && Underlying(convert.Operand.Type) is { IsEnum: true } type
            ? type
            : null;

    /// <summary>Whether a chain of conversions turns an enum into a different type — the number C# compares an enum as.</summary>
    internal static bool HidesEnum(Expression node)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            var from = Underlying(convert.Operand.Type);
            if (from.IsEnum && from != Underlying(convert.Type))
            {
                return true;
            }

            node = convert.Operand;
        }

        return false;
    }

    /// <summary>The enum-typed operand under the conversions <see cref="HidesEnum"/> found.</summary>
    internal static Expression Bare(Expression node)
    {
        while (!Underlying(node.Type).IsEnum && node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            node = convert.Operand;
        }

        return node;
    }

    internal static bool IsNullable(Expression? node) => node is not null && Nullable.GetUnderlyingType(node.Type) is not null;

    internal static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    internal static string? RecordKey(MethodCallExpression call) =>
        call is { Method.Name: "get_Item", Object: { } record, Arguments: [ConstantExpression { Value: string key }] } && record.Type == typeof(AlvoRecord) ? key : null;

    /// <summary>
    /// Recognizes a membership test: <c>Enumerable.Contains</c>, <c>MemoryExtensions.Contains</c> over an implicit span
    /// conversion (with no comparer or a <see langword="null"/> one), or the instance <c>Contains(T)</c> of a
    /// <see cref="List{T}"/>, a <see cref="HashSet{T}"/> or an <see cref="ICollection{T}"/>. A text is never a list.
    /// </summary>
    /// <remarks>
    /// A <see cref="HashSet{T}"/> carries its own comparer, which this shape cannot see: the caller evaluates the source
    /// and refuses a set whose comparer is not the default one.
    /// </remarks>
    internal static bool IsContains(MethodCallExpression call, [NotNullWhen(true)] out Expression? source, [NotNullWhen(true)] out Expression? value)
    {
        (source, value) = call switch
        {
            { Method.Name: "Contains", Object: null, Arguments: [var items, var item] } when IsSequence(call.Method.DeclaringType) => (Spanless(items), item),
            { Method.Name: "Contains", Object: null, Arguments: [var items, var item, ConstantExpression { Value: null }] } when call.Method.DeclaringType == typeof(MemoryExtensions) => (Spanless(items), item),
            { Method.Name: "Contains", Object: { } items, Arguments: [var item] } when IsCollectionContains(call.Method) => (items, item),
            _ => ((Expression?)null, (Expression?)null),
        };

        if (source is null || _texts.Contains(source.Type))
        {
            (source, value) = (null, null);
        }

        return source is not null;
    }

    private static bool IsCollectionContains(MethodInfo method) =>
        method.DeclaringType is { IsGenericType: true } declaring
        && _collections.Contains(declaring.GetGenericTypeDefinition())
        && method.GetParameters() is [var parameter]
        && parameter.ParameterType == declaring.GetGenericArguments()[0];

    private static bool IsSequence(Type? type) => type == typeof(Enumerable) || type == typeof(MemoryExtensions);

    private static Expression Spanless(Expression items) => items switch
    {
        MethodCallExpression { Method.Name: "op_Implicit", Arguments: [var inner] } => inner,
        UnaryExpression { NodeType: ExpressionType.Convert } convert => convert.Operand,
        _ => items,
    };

    private static bool IsTransparent(UnaryExpression unary)
    {
        if (unary.NodeType == ExpressionType.Quote)
        {
            return true;
        }

        if (IsNullable(unary.Operand) && unary.Type.IsValueType && !IsNullable(unary))
        {
            return false;
        }

        var from = Underlying(unary.Operand.Type);
        var to = Underlying(unary.Type);

        return from == to || to == typeof(object) || (!from.IsValueType && !to.IsValueType)
            || (from.IsEnum && Enum.GetUnderlyingType(from) == to) || Widens(from, to);
    }

    private static bool Widens(Type from, Type to) =>
        ((to == typeof(long) || to == typeof(decimal)) && _integral.Contains(from)) || (to == typeof(int) && _small.Contains(from));

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        private bool _found;

        internal bool Finds(Expression node)
        {
            Visit(node);

            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found |= node == parameter;

            return node;
        }
    }

    private sealed class ClockFinder : ExpressionVisitor
    {
        private Expression? _clock;

        internal Expression? Find(Expression node)
        {
            Visit(node);

            return _clock;
        }

        public override Expression? Visit(Expression? node)
        {
            if (_clock is null && node is not null && IsClock(node))
            {
                _clock = node;
            }

            return base.Visit(node);
        }
    }
}
