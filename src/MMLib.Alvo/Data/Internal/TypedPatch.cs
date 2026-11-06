using System.Collections.ObjectModel;
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>The one <see cref="IAlvoPatch{T}"/>: field names from the type's mapping, values as the fields hold them.</summary>
/// <typeparam name="T">The record type.</typeparam>
/// <remarks><see cref="Values"/> is a read-only view, so a caller cannot add a field past <see cref="Set{TValue}"/>'s checks.</remarks>
internal sealed class TypedPatch<T> : IAlvoPatch<T>
    where T : class
{
    private readonly Dictionary<string, object?> _values;

    private readonly ReadOnlyDictionary<string, object?> _view;

    /// <summary>Initializes an empty patch.</summary>
    public TypedPatch()
    {
        _values = new(StringComparer.Ordinal);
        _view = _values.AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Values => _view;

    /// <inheritdoc/>
    public IAlvoPatch<T> Set<TValue>(Expression<Func<T, TValue>> field, TValue value)
    {
        ArgumentNullException.ThrowIfNull(field);
        var name = Writable(TypedField.Of(field), nameof(field));
        _values[name] = RecordWriter.Field(name, value);

        return this;
    }

    private static string Writable(string name, string parameter) => RecordWriter.IsWritten(name, isUpdate: true)
        ? name
        : throw new ArgumentException($"'{name}' is managed by Alvo and cannot be patched.", parameter);
}
