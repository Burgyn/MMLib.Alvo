using System.Globalization;
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// A query's values, read on every call and typed as the field holds them (plan Ruling 25): an enum value becomes its
/// member's declared text, an integral widens to <see cref="long"/>, and a value no field type holds is refused.
/// </summary>
internal sealed partial class FilterTranslator
{
    private const string NoClockFix = "read the clock before the query and capture it: var now = DateTimeOffset.UtcNow; then x => x.DueAt < now.";

    private object? Value(Expression value, Type? enumType)
    {
        if (LambdaShape.FindClock(value) is { } clock)
        {
            throw Refuse(clock, "a query has no clock", NoClockFix);
        }

        return Typed(LambdaValues.Evaluate(value), enumType, value);
    }

    private object? Typed(object? raw, Type? enumType, Expression node) => raw switch
    {
        null => null,
        _ when enumType is not null => EnumValue(EnumMap.MemberFor(enumType, raw, out var reason) ?? throw Refuse(node, reason), node),
        Enum => throw Refuse(node, "an enum value compares only with a field of its enum", "compare the enum field itself, such as x => x.Status == OrderStatus.Approved."),
        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
        ulong big when big <= long.MaxValue => (long)big,
        string or bool or decimal or Guid or DateTimeOffset or DateOnly => raw,
        double or float => throw Refuse(node, $"a {raw.GetType().Name} value matches no field type", "use a decimal, such as 99.5m."),
        DateTime => throw Refuse(node, "a DateTime value matches no field type: it has no offset, so it names no single instant", "use a DateTimeOffset."),
        _ => throw Refuse(node, $"a {raw.GetType().Name} value matches no field type"),
    };

    private string EnumValue(Enum member, Expression node) => EnumMap.Of(member.GetType()).ValueOf(member)
        ?? throw Refuse(node, $"{member.GetType().Name} has no member {member}");
}
