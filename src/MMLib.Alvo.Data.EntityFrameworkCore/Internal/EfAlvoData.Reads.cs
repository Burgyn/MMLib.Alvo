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
    /// The enrichment of a caller-facing read — a page, a get, a write's echo or a replay's — or <see langword="null"/> when
    /// no enricher binds the entity. A write's echo is enriched with the write's own operation; its target rows, pre-images
    /// and event images never are.
    /// </summary>
    private IReadEnrichment? EnrichmentOf(string entity, DataOperation operation, AlvoContext context) =>
        _reads?.BeginEnrichment(entity, operation, context);

    /// <summary>
    /// One read's rows as the caller receives them: masked, unselected fields absent, every virtual field the row may carry
    /// present — <see langword="null"/> until an enricher fills it, so the key set of a read is stable — and then passed
    /// through the read's enrichment, if any.
    /// </summary>
    /// <param name="schema">The entity the rows belong to, or <see langword="null"/> when the applied schema does not declare it.</param>
    /// <param name="hidden">The fields the caller's policy hides.</param>
    /// <param name="unselected">The fields the caller's projection left out.</param>
    /// <param name="enrichment">The read's enrichment, or <see langword="null"/>.</param>
    private sealed class RowShaper(
        EntitySchema? schema, IReadOnlySet<string> hidden, IReadOnlySet<string> unselected, IReadEnrichment? enrichment)
    {
        private readonly string[] _virtuals = schema?.Fields
            .Where(field => field.IsVirtual && !hidden.Contains(field.Name) && !unselected.Contains(field.Name))
            .Select(field => field.Name)
            .ToArray() ?? [];

        internal AlvoRecord Shaped(Dictionary<string, object> row)
        {
            var record = WithVirtuals(RecordMaterializer.ToRecord(row, hidden, unselected));

            return enrichment is null ? record : enrichment.Enrich(record);
        }

        private AlvoRecord WithVirtuals(AlvoRecord record)
        {
            if (_virtuals.All(record.Values.ContainsKey))
            {
                return record;
            }

            var values = new Dictionary<string, object?>(record.Values, StringComparer.Ordinal);
            foreach (var name in _virtuals)
            {
                values.TryAdd(name, null);
            }

            return new AlvoRecord(values);
        }
    }
}
