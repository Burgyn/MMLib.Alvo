using System.Globalization;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// One field's value as a mapped property holds it — from what a read or an event image carries: an id, a day or an
/// instant as text, a whole number as a long, a decimal as a decimal or a whole long, an enum value as its declared text.
/// </summary>
/// <remarks>A missing field is not a null one: the message says which, and never echoes the value.</remarks>
internal static class RecordValue
{
    internal static object? Read(RecordShape shape, AlvoRecord record, MappedProperty property)
    {
        if (!record.TryGetValue(property.Field, out var raw))
        {
            throw Missing(shape, property);
        }

        if (raw is null)
        {
            return property.AcceptsNull ? null : throw Null(shape, property);
        }

        return Held(raw, property) ?? throw Unheld(shape, property, raw);
    }

    internal static InvalidOperationException Missing(RecordShape shape, MappedProperty property) => new(
        $"The record has no field '{property.Field}', which {shape.Name}.{property.Property.Name} maps to. A read leaves out a "
        + "field the caller may not see, and an event image leaves out what events.exclude names: map a type without it, or "
        + "read the record untyped.");

    private static InvalidOperationException Null(RecordShape shape, MappedProperty property) => new(
        $"Field '{property.Field}' is null, but {shape.Name}.{property.Property.Name} cannot hold null: declare it as {property.ValueType.Name}?.");

    private static InvalidOperationException Unheld(RecordShape shape, MappedProperty property, object raw) => new(
        property.Enum is { } map
            ? $"Field '{property.Field}' holds a value {map.Type.Name} has no member for: name the member's value with [AlvoEnumValue(\"…\")]."
            : $"Field '{property.Field}' holds {ShapeProblems.WithArticle(raw.GetType().Name)}, which {shape.Name}.{property.Property.Name} ({property.ValueType.Name}) cannot take.");

    private static object? Held(object raw, MappedProperty property)
    {
        if (property.Enum is { } map)
        {
            return raw is string text ? map.MemberOf(text) : raw.GetType() == map.Type ? raw : null;
        }

        return Scalar(raw, property.ValueType);
    }

    private static object? Scalar(object raw, Type target)
    {
        if (raw.GetType() == target)
        {
            return raw;
        }

        return target == typeof(long) ? Whole(raw)
            : target == typeof(int) ? Whole(raw) is { } whole && whole is >= int.MinValue and <= int.MaxValue ? (int)whole : null
            : target == typeof(decimal) ? Exact(raw)
            : target == typeof(Guid) ? raw is string text && Guid.TryParse(text, out var id) ? id : null
            : target == typeof(DateOnly) ? Day(raw)
            : target == typeof(DateTimeOffset) ? Instant(raw)
            : null;
    }

    private static long? Whole(object raw) => raw switch
    {
        long number => number,
        int number => number,
        decimal number when number == decimal.Truncate(number) && number >= long.MinValue && number <= long.MaxValue => (long)number,
        _ => null,
    };

    private static decimal? Exact(object raw) => raw switch
    {
        decimal number => number,
        long number => number,
        int number => number,
        double number when double.IsFinite(number) && Math.Abs(number) < 7.9e28 => (decimal)number,
        _ => null,
    };

    private static DateOnly? Day(object raw) => raw switch
    {
        DateTime instant => DateOnly.FromDateTime(instant),
        string text when DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) => day,
        _ => null,
    };

    private static DateTimeOffset? Instant(object raw) => raw switch
    {
        DateTime instant => new DateTimeOffset(instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime() : DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
        string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant) => instant,
        _ => null,
    };
}
