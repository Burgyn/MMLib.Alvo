using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MMLib.Alvo.Schema;
using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Assigns a create's sequence numbers, one allocation per sequenced field, on the create's own connection and inside its
/// own transaction — so the counter row stays locked until the create commits, and a rollback returns the number.
/// </summary>
/// <remarks>
/// It runs after the before-hooks, immediately before the row is added: a hook never sees a number, <c>per</c> reads the
/// row the hooks left, and the period is the write's own stamp in UTC.
/// </remarks>
/// <param name="dialect">The engine's dialect, which spells the allocation.</param>
/// <param name="table">The prefixed counter table.</param>
internal sealed class SequenceNumbers(IAlvoSqlDialect dialect, string table)
{
    /// <summary>Writes every sequenced field of <paramref name="schema"/> into <paramref name="row"/>.</summary>
    /// <param name="db">The create's context, inside its transaction.</param>
    /// <param name="schema">The entity being created.</param>
    /// <param name="row">The candidate row, after hooks.</param>
    /// <param name="instant">The write's stamp.</param>
    /// <param name="ct">A token to cancel the allocation.</param>
    internal Task AssignAsync(
        AlvoDataContext db, EntitySchema schema, IDictionary<string, object> row, DateTimeOffset instant, CancellationToken ct) =>
        AssignAsync(db, schema, [row], instant, ct);

    /// <summary>
    /// Writes every sequenced field of <paramref name="schema"/> into each of <paramref name="rows"/>, taking the counter
    /// rows in one canonical order — field by field in schema order, and within a field count by count in ordinal key
    /// order — and handing each count's numbers to its rows in request order.
    /// </summary>
    /// <remarks>
    /// <b>The canonical order is what keeps two concurrent batches from deadlocking.</b> Allocating in request order, a
    /// batch <c>[north, south]</c> and a batch <c>[south, north]</c> would each lock one branch's counter row and wait for
    /// the other's; PostgreSQL would abort one with <c>40P01</c>. Every writer — a single create takes one count per field,
    /// in schema order — now locks in the same total order, so no cycle can form.
    /// </remarks>
    /// <param name="db">The create's context, inside its transaction.</param>
    /// <param name="schema">The entity being created.</param>
    /// <param name="rows">The candidate rows, after hooks, in request order.</param>
    /// <param name="instant">The write's stamp.</param>
    /// <param name="ct">A token to cancel the allocation.</param>
    internal async Task AssignAsync(
        AlvoDataContext db, EntitySchema schema, IReadOnlyList<IDictionary<string, object>> rows, DateTimeOffset instant,
        CancellationToken ct)
    {
        foreach (var field in schema.Fields.Where(field => field.Sequence is not null))
        {
            await AssignFieldAsync(db, schema, field, rows, instant, ct).ConfigureAwait(false);
        }
    }

    private async Task AssignFieldAsync(
        AlvoDataContext db, EntitySchema schema, FieldSchema field, IReadOnlyList<IDictionary<string, object>> rows,
        DateTimeOffset instant, CancellationToken ct)
    {
        var counts = rows
            .GroupBy(row => Keys(schema, field, row, instant))
            .OrderBy(count => count.Key, CountKeysOrder.Instance);
        foreach (var count in counts)
        {
            foreach (var row in count)
            {
                var number = await NextAsync(db, count.Key, field.Sequence!.Start, ct).ConfigureAwait(false);
                row[field.Name] = field.Sequence.Format is { } format ? SequenceFormat.Render(format, number, instant) : number;
            }
        }
    }

    private static CountKeys Keys(EntitySchema schema, FieldSchema field, IDictionary<string, object> row, DateTimeOffset instant) => new(
        schema.Name,
        field.Name,
        SequenceFormat.KeyText(row.TryGetValue(AlvoDataContext.TenantIdColumn, out var tenant) ? tenant : null),
        SequenceFormat.KeyText(field.Sequence!.Per is { } per && row.TryGetValue(per, out var scope) ? scope : null),
        SequenceFormat.PeriodKey(field.Sequence.Period, instant));

    private async Task<long> NextAsync(AlvoDataContext db, CountKeys keys, long start, CancellationToken ct)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()
                ?? throw new InvalidOperationException("A sequence number is allocated only inside the create's transaction.");
            command.CommandText = dialect.SequenceAllocationStatement(table)
                ?? throw new InvalidOperationException($"{dialect.GetType().Name} cannot allocate a sequence number.");
            Bind(command, keys, start);
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
    }

    private static void Bind(DbCommand command, CountKeys keys, long start)
    {
        RelationalSqlBatch.AddParameter(command, "@entity", keys.Entity);
        RelationalSqlBatch.AddParameter(command, "@field", keys.Field);
        RelationalSqlBatch.AddParameter(command, "@tenant", keys.Tenant);
        RelationalSqlBatch.AddParameter(command, "@scope", keys.Scope);
        RelationalSqlBatch.AddParameter(command, "@period", keys.Period);
        RelationalSqlBatch.AddParameter(command, "@start", start);
    }

    private readonly record struct CountKeys(string Entity, string Field, string Tenant, string Scope, string Period);

    private sealed class CountKeysOrder : IComparer<CountKeys>
    {
        internal static CountKeysOrder Instance { get; } = new();

        public int Compare(CountKeys x, CountKeys y)
        {
            var tenant = string.CompareOrdinal(x.Tenant, y.Tenant);
            if (tenant != 0)
            {
                return tenant;
            }

            var scope = string.CompareOrdinal(x.Scope, y.Scope);
            return scope != 0 ? scope : string.CompareOrdinal(x.Period, y.Period);
        }
    }
}
