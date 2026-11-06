using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>
/// The whole of what an enricher can do to one returned row: remove a field that is not a framework column, or fill a
/// field the descriptor declares virtual with a value its type holds — and only when the row already carries it, so a
/// field the caller may not read, or did not select, never comes back.
/// </summary>
internal sealed class RowShaping(EntitySchema schema, AlvoRecord row, Func<FormatCatalog> formats)
{
    private readonly Dictionary<string, object?> _values = new(row.Values, StringComparer.Ordinal);

    internal string Entity => schema.Name;

    /// <summary>The row so far as <typeparamref name="T"/>; a mapped property whose field the row lacks reads its default.</summary>
    internal T As<T>()
        where T : class =>
        typeof(T) == typeof(AlvoRecord)
            ? (T)(object)ToRecord()
            : RecordShape.Of(typeof(T)).Materialize<T>(ToRecord(), static _ => true);

    /// <summary>Removes <paramref name="field"/> from the row.</summary>
    /// <exception cref="ReadShapingRefusal"><paramref name="field"/> is a framework column.</exception>
    internal void Hide(string field)
    {
        if (AlvoManagedColumns.For(schema).Contains(field))
        {
            throw new ReadShapingRefusal(HookFailedException.InvalidHide);
        }

        _values.Remove(field);
    }

    /// <summary>Fills the virtual <paramref name="field"/> when the row carries it.</summary>
    /// <exception cref="ReadShapingRefusal"><paramref name="field"/> is not virtual, or <paramref name="value"/> does not fit it.</exception>
    internal void SetVirtual(string field, object? value)
    {
        var declared = schema.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal));
        var (fits, held) = declared is { IsVirtual: true } ? Converted(declared, value) : (false, null);
        if (!fits)
        {
            throw new ReadShapingRefusal(HookFailedException.InvalidSet);
        }

        if (_values.ContainsKey(field))
        {
            _values[field] = held;
        }
    }

    internal AlvoRecord ToRecord() => new(new Dictionary<string, object?>(_values, StringComparer.Ordinal));

    private (bool Fits, object? Value) Converted(FieldSchema field, object? value)
    {
        object? held;
        try
        {
            held = HookSets.Coerce(field, RecordWriter.Field(field.Name, value));
        }
        catch (ArgumentException)
        {
            return (false, null);
        }

        return (Holds(field, held), held);
    }

    private bool Holds(FieldSchema field, object? value) =>
        value is null
        || (FieldClrType.Of(field).IsInstanceOfType(value) && new MutationTarget(field, formats(), Disclosable: true).Violation(value) is null);
}

/// <summary>A move an enricher may not make; the runner fails the read with <see cref="Reason"/>.</summary>
#pragma warning disable RCS1194 // Deliberately no standard constructors: a refusal always carries its reason.
internal sealed class ReadShapingRefusal(string reason) : Exception(reason)
#pragma warning restore RCS1194
{
    internal string Reason { get; } = reason;
}
