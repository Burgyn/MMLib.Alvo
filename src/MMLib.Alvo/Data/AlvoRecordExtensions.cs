using MMLib.Alvo.Data.Internal;

namespace MMLib.Alvo.Data;

/// <summary>Reads a record as a host type.</summary>
public static class AlvoRecordExtensions
{
    /// <summary>
    /// The record as a <typeparamref name="T"/>: each mapped property from its field (<see cref="AlvoFieldAttribute"/>, else
    /// the property's snake_case name), an enum from its declared value (<see cref="AlvoEnumValueAttribute"/>, else
    /// snake_case); an <see cref="IAlvoEntity"/> wraps the record instead, keeping every field.
    /// </summary>
    /// <typeparam name="T">A class with a parameterless constructor, a positional record, or an <see cref="IAlvoEntity"/>.</typeparam>
    /// <param name="record">The record, from a read or an event.</param>
    /// <returns>A new <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// A mapped field is missing from the record (a read leaves out what the caller may not see, an event image what
    /// <c>events.exclude</c> names), null where the property cannot hold null, or a value the property cannot take — the
    /// message names the field; or <typeparamref name="T"/> cannot be mapped at all.
    /// </exception>
    public static T As<T>(this AlvoRecord record)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(record);

        return RecordShape.Of(typeof(T)).Materialize<T>(record);
    }
}
