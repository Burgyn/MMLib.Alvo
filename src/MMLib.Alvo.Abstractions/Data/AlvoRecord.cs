namespace MMLib.Alvo.Data;

/// <summary>
/// An immutable snapshot of a row's field values, keyed by field name. Both CEL backends
/// (<c>MMLib.Alvo.Expressions.Internal.CelInterpreter</c> for <c>WITH CHECK</c>, and the SQL
/// predicate renderer for <c>USING</c>) read a row through this shape rather than a concrete
/// storage type, so the same expression tree evaluates identically over an EF entity, a
/// dynamic-entity JSON payload, or a hand-built test fixture.
/// </summary>
/// <remarks>
/// A field that is absent from <see cref="Values"/> is indistinguishable from a field present
/// with a <see langword="null"/> value — both read as <see langword="null"/> through
/// <see cref="this[string]"/> — because a CEL expression has no way to observe "unset" versus
/// "set to null", and the interpreter's null-collapsing comparison rule treats them identically.
/// </remarks>
/// <param name="Values">The field values, by field name.</param>
public sealed record AlvoRecord(IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>An empty record, with no fields set.</summary>
    public static AlvoRecord Empty { get; } = new(new Dictionary<string, object?>());

    /// <summary>Gets a field's value, or <see langword="null"/> when the field is absent.</summary>
    /// <param name="field">The field name.</param>
    public object? this[string field] => Values.TryGetValue(field, out var value) ? value : null;

    /// <summary>Attempts to read a field's value.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="value">The field's value when <paramref name="field"/> is present.</param>
    /// <returns><see langword="true"/> when this record contains <paramref name="field"/>.</returns>
    public bool TryGetValue(string field, out object? value) => Values.TryGetValue(field, out value);

    /// <summary>Returns a copy of this record with one field added or replaced.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="value">The field's new value.</param>
    public AlvoRecord With(string field, object? value)
    {
        var next = new Dictionary<string, object?>(Values) { [field] = value };
        return new AlvoRecord(next);
    }
}
