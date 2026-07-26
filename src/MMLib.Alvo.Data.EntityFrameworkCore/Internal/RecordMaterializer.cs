namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Turns a property-bag row into the <see cref="AlvoRecord"/> the port returns, dropping every masked
/// field's key. The value the engine returned for a masked field is already a projected SQL <c>NULL</c> —
/// the column was never read — and dropping the key too means a masked field is indistinguishable from a
/// field the entity never declared.
/// </summary>
/// <remarks>
/// <para>
/// The values need no conversion: EF's own type mapping shapes them, so a <c>uuid</c> column arrives as a
/// <see cref="Guid"/>, a timestamp as a <see cref="DateTimeOffset"/> and a decimal as a
/// <see cref="decimal"/> — on both engines, which is the single strongest argument for reading through EF
/// rather than through a hand-rolled reader (a raw SQLite reader over the identical statement returns
/// <see cref="string"/> for all three).
/// </para>
/// <para>
/// Dropping the key is a real second gate rather than a formality over a value that is already
/// <see langword="null"/>: it is the one that still holds if a row ever reaches this method from a source
/// that never applied the null projection.
/// </para>
/// </remarks>
internal static class RecordMaterializer
{
    internal static AlvoRecord ToRecord(IDictionary<string, object> row, IReadOnlySet<string> hiddenFields)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(hiddenFields);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in row)
        {
            if (!hiddenFields.Contains(field))
            {
                values[field] = value;
            }
        }

        return new AlvoRecord(values);
    }
}
