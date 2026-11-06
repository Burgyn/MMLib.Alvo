using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>The host's read extensions at the reads only this driver can place.</summary>
internal sealed partial class EfAlvoData
{
    /// <summary>
    /// The host narrowing of a caller-facing read — a page, its count and its cursor's anchor, a get, a write's echo and a
    /// replay's — or <see langword="null"/>. Never asked for a write's target rows, its pre-image or an unmasked read: writes
    /// are not narrowed.
    /// </summary>
    private ReadNarrowing? NarrowingOf(string entity, DataOperation operation, AlvoContext context) =>
        _reads?.Narrowing(entity, operation, context);

    /// <summary>
    /// A materialized row as the caller receives it: masked, unselected fields absent, and every virtual field it may carry
    /// present — <see langword="null"/> until an enricher fills it, so the key set of a read is stable.
    /// </summary>
    /// <param name="row">The row as the statement returned it.</param>
    /// <param name="schema">The entity the row belongs to, or <see langword="null"/> when the applied schema does not declare it.</param>
    /// <param name="hidden">The fields the caller's policy hides.</param>
    /// <param name="unselected">The fields the caller's projection left out.</param>
    private static AlvoRecord Shaped(
        Dictionary<string, object> row, EntitySchema? schema, IReadOnlySet<string> hidden, IReadOnlySet<string> unselected)
    {
        var record = RecordMaterializer.ToRecord(row, hidden, unselected);
        var missing = schema?.Fields
            .Where(field => field.IsVirtual && !hidden.Contains(field.Name) && !unselected.Contains(field.Name))
            .Where(field => !record.Values.ContainsKey(field.Name))
            .ToList() ?? [];

        return missing.Aggregate(record, (shaped, field) => shaped.With(field.Name, null));
    }
}
