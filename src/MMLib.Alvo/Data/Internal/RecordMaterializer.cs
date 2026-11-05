using System.Reflection;

namespace MMLib.Alvo.Data.Internal;

/// <summary>Builds a host type from a record through the constructor its shape chose, then its remaining public setters.</summary>
/// <remarks>
/// The host's own constructor or setter exception surfaces as itself, not wrapped in a <see cref="TargetInvocationException"/>.
/// A field missing from the record is refused, unless the caller's predicate lets its property be absent: that property is left
/// at its type's default (a hook's image of a row not yet stored lacks its id, its audit columns and every omitted null).
/// </remarks>
internal static class RecordMaterializer
{
    internal static object Create(RecordShape shape, AlvoRecord record, Func<MappedProperty, bool>? mayBeAbsent = null)
    {
        if (shape.Problems.Count > 0)
        {
            throw new RecordUnreadableException($"{shape.Name} cannot be read from a record: {shape.Problems[0].Message} {shape.Problems[0].Fix}");
        }

        var absent = Absent(shape, record, mayBeAbsent);

        return shape.IsWrapper ? Wrapped(shape, WithDefaults(record, absent)) : Built(shape, record, absent);
    }

    private static object Wrapped(RecordShape shape, AlvoRecord record)
    {
        var missing = shape.Properties.FirstOrDefault(property => !record.TryGetValue(property.Field, out _));

        return missing is null ? Construct(shape, [record]) : throw RecordValue.Missing(shape, missing);
    }

    private static object Built(RecordShape shape, AlvoRecord record, IReadOnlyList<MappedProperty> absent)
    {
        var instance = Construct(shape, [.. shape.Arguments.Select(property => Value(shape, record, property, absent))]);
        foreach (var property in shape.Settable)
        {
            property.Property.SetValue(instance, Value(shape, record, property, absent), BindingFlags.DoNotWrapExceptions, binder: null, index: null, culture: null);
        }

        return instance;
    }

    private static object? Value(RecordShape shape, AlvoRecord record, MappedProperty property, IReadOnlyList<MappedProperty> absent) =>
        absent.Contains(property) ? Default(property) : RecordValue.Read(shape, record, property);

    /// <summary>The mapped properties whose field the record lacks and the caller lets be absent.</summary>
    private static MappedProperty[] Absent(RecordShape shape, AlvoRecord record, Func<MappedProperty, bool>? mayBeAbsent) =>
        mayBeAbsent is null
            ? []
            : [.. shape.Properties.Where(property => mayBeAbsent(property) && !record.TryGetValue(property.Field, out _))];

    private static AlvoRecord WithDefaults(AlvoRecord record, IReadOnlyList<MappedProperty> absent) =>
        absent.Aggregate(record, (filled, property) => filled.With(property.Field, Default(property)));

    private static object? Default(MappedProperty property) =>
        property.Property.PropertyType.IsValueType ? Activator.CreateInstance(property.Property.PropertyType) : null;

    private static object Construct(RecordShape shape, object?[] arguments) =>
        shape.Constructor!.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null);
}
