using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

using System.Collections;
using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Expressions;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>The calls a <c>when</c> may make — <c>changed()</c>, a role test, the ordinal text tests, a constant list's <c>Contains</c> — and its constants.</summary>
internal sealed partial class ConditionTranslator
{
    private const string ConstantFix = "make it a const or a static readonly field, or write the condition as a CEL string — when: \"…\".";
    private const string ClockReason = "a condition is judged after the commit, and conditions have no clock yet";
    private const string ClockFix = "compare against a field the write stored, or check the time inside the handler body.";

    private static readonly FrozenDictionary<string, string> _textTests = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(string.StartsWith)] = "startsWith",
        [nameof(string.EndsWith)] = "endsWith",
        [nameof(string.Contains)] = "contains",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private CelNode Call(MethodCallExpression call) => call switch
    {
        { Method.Name: nameof(EventCondition<>.Changed), Object: var owner } when owner == _event => Changed(call),
        { Method.Name: nameof(AlvoConditionCaller.HasRole), Object: { } caller } when caller.Type == typeof(AlvoConditionCaller) => HasRole(call),
        { Object: { } text } when text.Type == typeof(string) && _textTests.TryGetValue(call.Method.Name, out var name) => TextTest(call, name),
        _ when LambdaShape.IsContains(call, out var source, out var value) => AnyOf(call, source, value),
        _ when LambdaShape.IsClock(call) => throw Refuse(call, ClockReason, ClockFix),
        _ => throw Refuse(call, $"method '{call.Method.Name}' has no CEL equivalent", TranslationRefusal.WhenFix),
    };

    private CelNode Changed(MethodCallExpression call)
    {
        if (_verb != HandlerVerb.Updated)
        {
            throw Refuse(call, $"changed() compares the record before and after the write, and On{_verb} carries one of them", "register the handler with OnUpdated.");
        }

        var fields = call.Arguments[0] is NewArrayExpression array
            ? array.Expressions.Select(ChangedField).ToList()
            : throw Refuse(call, "changed() takes its properties inline", "write e.Changed(x => x.Status, x => x.Total).");
        return fields.Count > 0
            ? AnyTrue(fields)
            : throw Refuse(call, "changed() names no property", "name at least one: e.Changed(x => x.Status).");
    }

    private CelNode ChangedField(Expression argument)
    {
        var lambda = LambdaShape.Unwrapped(argument) as LambdaExpression;
        var body = lambda is null ? null : LambdaShape.Unwrapped(lambda.Body);

        return body is MemberExpression { Expression: ParameterExpression own } member && own == lambda!.Parameters[0] && _shape.Mapped(member.Member) is { } mapped
            ? new CelChanged(FieldName(argument, mapped.Field))
            : throw Refuse(argument, "changed() takes a mapped property of the record, such as x => x.Status", TranslationRefusal.WhenFix);
    }

    private CelBinary HasRole(MethodCallExpression call)
    {
        if (!IsEventCaller(call.Object))
        {
            throw Refuse(call, "HasRole is read from the event's own caller", "write e.Caller.HasRole(\"role\").");
        }

        return LambdaValues.TryConstant(call.Arguments[0], out var role, out var reason) && role is string { Length: > 0 } name
            ? new CelBinary(CelBinaryOperator.In, new CelLiteral(CelValueType.String, name), new CelContextRef(CelContextValue.UserRoles, CelValueType.StringList))
            : throw Refuse(call, reason.Length > 0 ? reason : "a role is a non-empty constant", ConstantFix);
    }

    private CelCall TextTest(MethodCallExpression call, string name)
    {
        var ordinal = call.Arguments.Count == 1 || call.Arguments is [_, ConstantExpression { Value: StringComparison.Ordinal }];
        if (!ordinal || call.Arguments[0].Type != typeof(string))
        {
            throw Refuse(call, $"only the ordinal comparison translates: CEL's {name} compares characters exactly", "call the one-argument overload, or pass StringComparison.Ordinal.");
        }

        return new CelCall(name, [Operand(call.Object!), Operand(call.Arguments[0])]);
    }

    private CelNode AnyOf(MethodCallExpression call, Expression source, Expression value)
    {
        if (LambdaShape.Touches(source, _event) || !LambdaShape.Touches(value, _event))
        {
            throw Refuse(call, "Contains translates only as a constant list containing a field", TranslationRefusal.WhenFix);
        }

        var list = OrderedList(source);
        if (list.Count > MaxListValues)
        {
            throw Refuse(call, $"it lists {list.Count} values, more than the {MaxListValues} a condition of {CelParser.MaxSourceLength} characters can hold", "test fewer values, or move the check into the handler body.");
        }

        var field = LambdaShape.HidesEnum(value) ? LambdaShape.Bare(value) : value;
        var enumType = field == value ? null : LambdaShape.Underlying(field.Type);
        var tests = list.Cast<object?>()
            .Select(item => Typed(call, CelBinaryOperator.Equal, Operand(field), Literal(source, AsMember(source, enumType, item))))
            .ToList();
        return tests.Count > 0
            ? AnyTrue(tests)
            : throw Refuse(call, "an empty list matches nothing", "remove the condition, or name at least one value.");
    }

    private IList OrderedList(Expression source)
    {
        if (!LambdaValues.TryConstant(source, out var items, out var reason))
        {
            throw Refuse(source, reason, ConstantFix);
        }

        return items as IList ?? throw Refuse(
            source,
            items is null ? "the list is null" : $"a {items.GetType().Name} has no fixed order, so the condition's text would change from run to run",
            "use an array or a List<T>.");
    }

    private object? AsMember(Expression source, Type? enumType, object? item) =>
        enumType is not null && item is not null and not Enum ? EnumMember(source, enumType, item) : item;

    private static CelNode AnyTrue(IEnumerable<CelNode> tests) =>
        tests.Aggregate((left, right) => new CelBinary(CelBinaryOperator.Or, left, right));

    private CelNode Constant(Expression node)
    {
        if (LambdaShape.FindClock(node) is { } clock)
        {
            throw Refuse(clock, ClockReason, ClockFix);
        }

        return LambdaValues.TryConstant(node, out var value, out var reason) ? Literal(node, value) : throw Refuse(node, reason, ConstantFix);
    }

    private CelNode Literal(Expression node, object? value) => value switch
    {
        null => throw Refuse(node, "null is tested with == null or != null against a field", TranslationRefusal.WhenFix),
        string text => new CelLiteral(CelValueType.String, text),
        bool flag => new CelLiteral(CelValueType.Bool, flag),
        Enum member => EnumText(node, member),
        long.MinValue => throw Refuse(node, "long.MinValue has no CEL literal", ConstantFix),
        sbyte or byte or short or ushort or int or uint or long => Signed(CelValueType.Int, Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong big when big <= long.MaxValue => Signed(CelValueType.Int, (long)big),
        decimal number => Signed(CelValueType.Decimal, number),
        Guid id => new CelLiteral(CelValueType.Uuid, id),
        DateTimeOffset instant => new CelCall("timestamp", [new CelLiteral(CelValueType.String, Rfc3339(instant))]),
        double or float => throw Refuse(node, "a binary floating-point constant has no exact CEL literal", "use a decimal constant, such as 99.5m."),
        DateTime => throw Refuse(node, "a DateTime has no offset, so it names no single instant", "use a DateTimeOffset."),
        _ => throw Refuse(node, $"a {value.GetType().Name} constant has no CEL literal", TranslationRefusal.WhenFix),
    };

    private static CelNode Signed(CelValueType type, long number) => number < 0
        ? new CelUnary(CelUnaryOperator.Negate, new CelLiteral(type, -number))
        : new CelLiteral(type, number);

    private static CelNode Signed(CelValueType type, decimal number) => number < 0m
        ? new CelUnary(CelUnaryOperator.Negate, new CelLiteral(type, -number))
        : new CelLiteral(type, number);

    private static string Rfc3339(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "Z";
}
