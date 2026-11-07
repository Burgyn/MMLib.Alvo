using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The in-memory store's sequence counters: the same counts the relational drivers keep in <c>&lt;prefix&gt;_sequences</c>,
/// held in a dictionary under the store's own lock, so a host's unit test reads the numbers its creates would get.
/// </summary>
/// <remarks>
/// Called with the store's lock held and only on the path that then stores the row, so a refused create takes no number.
/// The period is the row's own <c>created_at</c> stamp when it has one — the instant the relational drivers key it on —
/// so a period boundary never falls between the two.
/// </remarks>
internal sealed class InMemorySequences
{
    private readonly Dictionary<(string Entity, string Field, string Tenant, string Scope, string Period), long> _last = [];

    /// <summary><paramref name="row"/> with every sequenced field of <paramref name="schema"/> assigned its next number.</summary>
    /// <param name="schema">The entity being created.</param>
    /// <param name="row">The row about to be stored.</param>
    /// <param name="now">The clock's reading, for a row that carries no <c>created_at</c> stamp of its own.</param>
    internal AlvoRecord Numbered(EntitySchema schema, AlvoRecord row, DateTimeOffset now)
    {
        var sequenced = schema.Fields.Where(field => field.Sequence is not null).ToList();
        if (sequenced.Count == 0)
        {
            return row;
        }

        var values = new Dictionary<string, object?>(row.Values, StringComparer.Ordinal);
        var instant = values.TryGetValue(AlvoManagedColumns.CreatedAt, out var created) && created is DateTimeOffset stamped ? stamped : now;
        foreach (var field in sequenced)
        {
            var number = Next(schema, field, values, instant);
            values[field.Name] = field.Sequence!.Format is { } format
                ? SequenceFormat.Render(format, number, instant, field.Sequence.TimeZone)
                : number;
        }

        return new AlvoRecord(values);
    }

    private long Next(EntitySchema schema, FieldSchema field, Dictionary<string, object?> values, DateTimeOffset instant)
    {
        var sequence = field.Sequence!;
        var key = (
            schema.Name,
            field.Name,
            KeyOf(values, AlvoManagedColumns.TenantId),
            sequence.Per is { } per ? KeyOf(values, per) : string.Empty,
            SequenceFormat.PeriodKey(sequence.Period, instant, sequence.TimeZone));
        var number = _last.TryGetValue(key, out var last) ? last + 1 : sequence.Start;
        _last[key] = number;
        return number;
    }

    private static string KeyOf(Dictionary<string, object?> values, string field) =>
        values.TryGetValue(field, out var value) && value is not null ? SequenceFormat.KeyText(value) : string.Empty;
}
