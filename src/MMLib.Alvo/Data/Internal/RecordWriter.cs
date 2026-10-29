using MMLib.Alvo.Schema;

using System.Globalization;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// A typed record as the field values a write sends — the read direction of <see cref="RecordValue"/> reversed: an enum
/// member as its declared value, a whole number as a <see cref="long"/>, every other value as it is — and the fields Alvo
/// manages left out.
/// </summary>
internal static class RecordWriter
{
    /// <summary>
    /// Whether a write sends <paramref name="field"/>: never a column Alvo manages, except the one a caller may write — a
    /// create's <c>tenant_id</c> — by the port's own rule (<see cref="AlvoManagedColumns.IsCallerWritable"/>).
    /// </summary>
    internal static bool IsWritten(string field, bool isUpdate) =>
        !AlvoManagedColumns.All.Contains(field) || AlvoManagedColumns.IsCallerWritable(field, isUpdate);

    /// <summary>The fields <paramref name="record"/> writes: a wrapper's record, or a plain type's mapped properties.</summary>
    /// <remarks>
    /// A create leaves out every field whose value is <see langword="null"/>, so the field's descriptor default applies —
    /// or, with none, the column stays empty — exactly as an untyped create that does not name the field. A replace sends
    /// every field, <see langword="null"/> included, because it replaces the whole row. A non-nullable property's CLR
    /// default (<c>0</c>, <see langword="false"/>, the first enum member) is a value, and is sent (Ruling 36).
    /// </remarks>
    /// <exception cref="InvalidOperationException">The type has a mapping problem no descriptor can cure.</exception>
    internal static Dictionary<string, object?> Values<T>(T record, bool isCreate)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        var shape = RecordShape.Of(typeof(T));
        if (shape.Problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{shape.Name} cannot be written: {string.Join(" ", shape.Problems.Select(problem => problem.Message))}");
        }

        var fields = record is IAlvoEntity wrapper
            ? wrapper.Record.Values.Select(pair => (Name: pair.Key, Value: pair.Value))
            : shape.Properties.Select(property => (Name: property.Field, Value: Field(property, property.Property.GetValue(record))));

        return fields
            .Where(field => IsWritten(field.Name, isUpdate: !isCreate) && !(isCreate && field.Value is null))
            .ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
    }

    /// <summary>One property's value as its field holds it.</summary>
    /// <exception cref="ArgumentException">An enum member names no declared value.</exception>
    internal static object? Field(MappedProperty property, object? value) => value switch
    {
        null => null,
        Enum member when property.Enum is { } map => map.ValueOf(member)
            ?? throw new ArgumentException($"{member.GetType().Name}.{member} names no value of field '{property.Field}': name it with [AlvoEnumValue(\"…\")].", nameof(value)),
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        _ => value,
    };

    /// <summary>The stored record as <typeparamref name="T"/> — or <see langword="null"/> when the caller's read-back lacks a field <typeparamref name="T"/> maps.</summary>
    /// <remarks>
    /// A write answers the row as the caller's own <c>get</c> would — only its id when the rule does not return the row, and
    /// without a hidden field. The write has happened; answering <see langword="null"/> rather than throwing keeps it from
    /// looking failed, and inside a unit from being rolled back by an exception the host did not cause.
    /// </remarks>
    internal static T? Readable<T>(AlvoRecord stored)
        where T : class
    {
        var shape = RecordShape.Of(typeof(T));

        return shape.IsWrapper || shape.Properties.All(property => stored.Values.ContainsKey(property.Field)) ? stored.As<T>() : null;
    }
}
