using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// A post-image value as the CEL runtime value a literal of its field's declared type has, so a write probe binds it as
/// the column's own comparison binds such a literal: <see cref="Guid"/> for <c>uuid</c>/<c>ref</c>, <see cref="decimal"/>,
/// <see cref="long"/>, <see cref="bool"/>, <see cref="string"/>, and a UTC <see cref="DateTimeOffset"/> for
/// <c>datetime</c> and <c>date</c> (midnight).
/// </summary>
/// <remarks>
/// A post-image arrives weakly typed — a GUID as text, a number as a double, an instant as a <see cref="DateTime"/>.
/// <see langword="null"/> means the value is absent or does not convert; the renderer then judges the comparison false,
/// as the interpreter's null rule does, and binds nothing.
/// </remarks>
internal static class RelationImageValue
{
    /// <summary>The declared-type value of <paramref name="field"/> in <paramref name="row"/>, or <see langword="null"/>.</summary>
    /// <param name="entity">The entity the row belongs to.</param>
    /// <param name="field">The field.</param>
    /// <param name="row">The post-image.</param>
    internal static object? Of(EntitySchema entity, string field, AlvoRecord row) =>
        Declared(entity, field) is { } declared ? Convert(Unwrapped(row[field]), CelFieldType.Of(declared)) : null;

    private static FieldSchema? Declared(EntitySchema entity, string field) =>
        entity.Fields.FirstOrDefault(declared => string.Equals(declared.Name, field, StringComparison.Ordinal))
        ?? (string.Equals(field, AlvoManagedColumns.Id, StringComparison.Ordinal) ? _id : null);

    private static readonly FieldSchema _id = new() { Name = AlvoManagedColumns.Id, Type = FieldType.Uuid };

    private static object? Convert(object? value, CelValueType type) => value is null ? null : type switch
    {
        CelValueType.Uuid => AsGuid(value),
        CelValueType.Decimal => AsDecimal(value),
        CelValueType.Int => AsLong(value),
        CelValueType.Bool => value as bool?,
        CelValueType.String => value as string ?? (value is Enum ? value.ToString() : null),
        CelValueType.Timestamp => AsInstant(value),
        _ => null,
    };

    private static object? Unwrapped(object? value) => value is JsonElement element ? FromJson(element) : value;

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
        _ => null,
    };

    private static Guid? AsGuid(object value) => value switch
    {
        Guid guid => guid,
        string text when Guid.TryParse(text, out var parsed) => parsed,
        _ => null,
    };

    private static decimal? AsDecimal(object value)
    {
        try
        {
            return value switch
            {
                decimal number => number,
                string text when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
                IConvertible number and not string and not bool => number.ToDecimal(CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static long? AsLong(object value) =>
        AsDecimal(value) is { } number && decimal.Truncate(number) == number && number is >= long.MinValue and <= long.MaxValue
            ? (long)number
            : null;

    private static DateTimeOffset? AsInstant(object value) => value switch
    {
        DateTimeOffset instant => instant.ToUniversalTime(),
        DateTime time => new DateTimeOffset(time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime()),
        DateOnly date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) =>
            parsed.ToUniversalTime(),
        _ => null,
    };
}
