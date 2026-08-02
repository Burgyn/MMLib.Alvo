namespace MMLib.Alvo.Data;

/// <summary>
/// An immutable snapshot of a row's field values, keyed by field name. Both CEL backends
/// (<c>MMLib.Alvo.Expressions.Internal.CelInterpreter</c> for <c>WITH CHECK</c>, and the SQL
/// predicate renderer for <c>USING</c>) read a row through this shape rather than a concrete
/// storage type, so the same expression tree evaluates identically over an EF entity, a
/// dynamic-entity JSON payload, or a hand-built test fixture.
/// </summary>
/// <remarks>
/// <para>
/// A field that is absent from <see cref="Values"/> is indistinguishable from a field present
/// with a <see langword="null"/> value — both read as <see langword="null"/> through
/// <see cref="this[string]"/> — because a CEL expression has no way to observe "unset" versus
/// "set to null", and the interpreter's null-collapsing comparison rule treats them identically.
/// A field present with the ADO.NET sentinel <see cref="DBNull"/>.<see cref="DBNull.Value"/> (as
/// a row hydrated straight from a <c>DbDataReader</c> would carry a SQL <c>NULL</c>) is
/// normalized to <see langword="null"/> for the same reason — <c>has(field)</c> must see a SQL
/// <c>NULL</c> exactly like any other absent-or-null field, never as "present".
/// </para>
/// <para>
/// Field names compare with <see cref="StringComparer.Ordinal"/>, matching the interpreter's own
/// ordinal string semantics, regardless of what comparer the source dictionary was built with —
/// a case-insensitive <see cref="Dictionary{TKey,TValue}"/> is re-keyed ordinally at construction
/// so a record and every <see cref="With"/> copy of it agree on every lookup. Re-keying only
/// applies to a concrete <see cref="Dictionary{TKey,TValue}"/> input, whose comparer can be
/// inspected; any other <see cref="IReadOnlyDictionary{TKey,TValue}"/> implementation is trusted
/// to already resolve keys the way its caller intends.
/// </para>
/// <para>
/// Two records are equal when they carry the same set of field/value pairs — key order and the
/// concrete backing dictionary type do not matter.
/// </para>
/// </remarks>
public sealed record AlvoRecord
{
    /// <summary>Initializes a new record from a set of field values.</summary>
    /// <param name="values">The field values, by field name.</param>
    public AlvoRecord(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = values is Dictionary<string, object?> dictionary
            ? new Dictionary<string, object?>(dictionary, StringComparer.Ordinal)
            : values;
    }

    /// <summary>Gets the field values, by field name.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>An empty record, with no fields set.</summary>
    public static AlvoRecord Empty { get; } = new(new Dictionary<string, object?>());

    /// <summary>Gets a field's value, or <see langword="null"/> when the field is absent or a SQL <c>NULL</c>.</summary>
    /// <param name="field">The field name.</param>
    public object? this[string field] => Normalize(Values.TryGetValue(field, out var value) ? value : null);

    /// <summary>Attempts to read a field's value.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="value">The field's value (already <see cref="DBNull"/>-normalized) when <paramref name="field"/> is present.</param>
    /// <returns><see langword="true"/> when this record contains <paramref name="field"/>.</returns>
    public bool TryGetValue(string field, out object? value)
    {
        var found = Values.TryGetValue(field, out var raw);
        value = Normalize(raw);
        return found;
    }

    /// <summary>Returns a copy of this record with one field added or replaced.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="value">The field's new value.</param>
    public AlvoRecord With(string field, object? value)
    {
        var next = new Dictionary<string, object?>(Values) { [field] = value };
        return new AlvoRecord(next);
    }

    /// <inheritdoc/>
    public bool Equals(AlvoRecord? other)
    {
        if (other is null)
        {
            return false;
        }

        if (Values.Count != other.Values.Count)
        {
            return false;
        }

        foreach (var key in Values.Keys)
        {
            if (!other.Values.ContainsKey(key) || !Equals(this[key], other[key]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in Values.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(this[key]);
        }

        return hash.ToHashCode();
    }

    private static object? Normalize(object? value) => value is DBNull ? null : value;
}
