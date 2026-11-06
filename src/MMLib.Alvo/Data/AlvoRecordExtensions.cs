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

    /// <summary>
    /// One field of the record as a <typeparamref name="T"/>, converted exactly as <see cref="As{T}"/> converts a mapped
    /// property.
    /// </summary>
    /// <remarks>
    /// The typed read of a single field, for host code and generated typed classes alike: a whole number, an id or a day
    /// stored as text, an instant and an enum's declared value (<see cref="AlvoEnumValueAttribute"/>, else snake_case) are
    /// converted; a null answers <see langword="default"/> when <typeparamref name="T"/> takes null.
    /// </remarks>
    /// <typeparam name="T">The field's host type: <see cref="string"/>, <see cref="long"/>, <see cref="int"/>, <see cref="decimal"/>,
    /// <see cref="bool"/>, <see cref="Guid"/>, <see cref="DateOnly"/>, <see cref="DateTimeOffset"/>, an enum, or one of them nullable.</typeparam>
    /// <param name="record">The record, from a read, an event or a hook.</param>
    /// <param name="field">The field's name.</param>
    /// <returns>The field's value.</returns>
    /// <exception cref="InvalidOperationException">
    /// The record has no such field, holds null where <typeparamref name="T"/> cannot, or a value it cannot take — the
    /// message names the field, never the value.
    /// </exception>
    public static T Get<T>(this AlvoRecord record, string field)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (!record.TryGetValue(field, out var raw))
        {
            throw new InvalidOperationException(
                $"The record has no field '{field}'. A record not stored yet has no id or audit columns, and a read leaves out a field the caller may not see.");
        }

        return raw is null ? NullOf<T>(field) : (T)(RecordValue.Convert(raw, typeof(T)) ?? throw Unheld<T>(field, raw));
    }

    private static T NullOf<T>(string field) => default(T) is null
        ? default!
        : throw new InvalidOperationException($"Field '{field}' is null, but {typeof(T).Name} cannot hold null.");

    private static InvalidOperationException Unheld<T>(string field, object raw) => new(
        $"Field '{field}' holds {ShapeProblems.WithArticle(raw.GetType().Name)}, which {Described(typeof(T))} cannot take.");

    private static string Described(Type type) =>
        Nullable.GetUnderlyingType(type) is { } held ? held.Name + "?" : type.Name;
}
