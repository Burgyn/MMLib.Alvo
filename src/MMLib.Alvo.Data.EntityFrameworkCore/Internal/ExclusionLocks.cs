using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MMLib.Alvo.Schema;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Serializes writers of one resource before a write that an exclusion judges (HX-12 D3): one exclusive transaction lock per
/// exclusion and equality-value combination the written rows hold, taken in ascending key order, immediately before the
/// write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> PostgreSQL checks an exclusion against rows other transactions have not committed by waiting for them; two
/// writers that each inserted before checking wait for each other, and the engine breaks the cycle with a <c>40P01</c>
/// deadlock — a 500 where the caller should see one 409. Queuing them first on one key per resource turns the second into
/// an ordinary check against a committed row.
/// </para>
/// <para>
/// <b>An engine whose dialect answers no lock statement</b> — SQLite, whose single writer already sees every committed row —
/// takes nothing. A key is a hash, so two resources may share one and queue needlessly; never the reverse.
/// </para>
/// </remarks>
internal static class ExclusionLocks
{
    /// <summary>Takes the locks <paramref name="rows"/> need on <paramref name="schema"/>'s exclusions, inside the write's transaction.</summary>
    /// <param name="db">The write's context, inside its transaction.</param>
    /// <param name="dialect">The engine's dialect, which spells the lock.</param>
    /// <param name="schema">The entity being written.</param>
    /// <param name="rows">The rows as the write will leave them, each as a lookup of a column's value.</param>
    /// <param name="ct">A token to cancel the wait.</param>
    internal static async Task TakeAsync(
        AlvoDataContext db, IAlvoSqlDialect dialect, EntitySchema schema, IEnumerable<Func<string, object?>> rows,
        CancellationToken ct)
    {
        if (!schema.Indexes.Any(index => index.Exclude is not null))
        {
            return;
        }

        var keys = Keys(schema, rows).Order().ToList();
        if (keys.Count == 0 || dialect.TransactionLockStatement([.. keys.Select((_, i) => $"@xk{i}")], exclusive: true) is not { } sql)
        {
            return;
        }

        await ExecuteAsync(db, sql, keys, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(AlvoDataContext db, string sql, List<long> keys, CancellationToken ct)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()
                ?? throw new InvalidOperationException("An exclusion lock is taken only inside the write's transaction.");
            command.CommandText = sql;
            for (var i = 0; i < keys.Count; i++)
            {
                RelationalSqlBatch.AddParameter(command, $"@xk{i}", keys[i]);
            }

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static IEnumerable<long> Keys(EntitySchema schema, IEnumerable<Func<string, object?>> rows) =>
        (from index in schema.Indexes.Where(index => index.Exclude is not null)
         let name = IndexNames.Exclusion(schema, index)
         let columns = DescriptorModelBuilder.UniqueColumns(schema, index.Fields)
         from row in rows
         select Key(name, columns, row)).Distinct();

    private static long Key(string name, IEnumerable<string> columns, Func<string, object?> row) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            name + "|" + string.Join("|", columns.Select(column => row(column) is { } value ? SequenceFormat.KeyText(value) : string.Empty)))));
}
