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
    /// <summary>Assembles the record one row becomes, dropping every key neither set admits.</summary>
    /// <param name="row">The property-bag row the engine returned.</param>
    /// <param name="hiddenFields">The field mask the caller's policy resolved.</param>
    /// <param name="unselectedFields">
    /// The declared fields the caller's projection excluded; empty when it excluded none.
    /// </param>
    /// <remarks>
    /// <b>Required rather than defaulted</b>, though six of the seven call sites pass an empty set. A
    /// default would mean the author who later adds a projection to a single-row read is never made to look
    /// at <c>GetAsync</c>'s call site — and a projection that narrowed that statement while its
    /// materialization went on returning every key is the advisory-member defect
    /// <see cref="AlvoQuery.Select"/> exists to avoid, one layer down.
    /// </remarks>
    internal static AlvoRecord ToRecord(
        IDictionary<string, object> row,
        IReadOnlySet<string> hiddenFields,
        IReadOnlySet<string> unselectedFields)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(hiddenFields);
        ArgumentNullException.ThrowIfNull(unselectedFields);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in row)
        {
            if (!hiddenFields.Contains(field) && !unselectedFields.Contains(field))
            {
                values[field] = value;
            }
        }

        return new AlvoRecord(values);
    }
}
