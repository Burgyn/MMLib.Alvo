using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MMLib.Alvo.Schema;
using System.Buffers.Binary;
using System.Globalization;
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
/// an ordinary check against a committed row. Measured (issue 478): two transaction units of one room, each holding an
/// uncommitted booking when it writes a second that overlaps the other's first, deadlock every time without the keys and
/// end in one commit and one 409 with them (<c>PostgreSqlExclusionLockTests</c>). Two single-statement creates rarely meet
/// that cycle — the insert and its check are one statement apart — so the concurrency facts pass without the keys.
/// </para>
/// <para>
/// <b>What they do not cover.</b> Two units that each hold one resource and then write the other's, in opposite order, still
/// deadlock: each holds its first resource's key and waits for the other's, as they would wait for each other's rows
/// without the keys. A unit orders its own writes; a batch's keys are sorted.
/// </para>
/// <para>
/// <b>An engine whose dialect answers no lock statement</b> — SQLite, whose single writer already sees every committed row —
/// takes nothing, and the dialect is asked before any key is hashed. A key is a hash, so two resources may share one and
/// queue needlessly; never the reverse.
/// </para>
/// <para>
/// <b>Order, and its one residual.</b> Every writer takes its row lock and sequence counters before these keys. A parent's
/// host <c>beforeUpdate</c> hook that creates a child booking holds the parent row and then waits for a key, while a plain
/// booking create holds the key and then locks the parent in its rollup recompute — a deadlock D3 introduces for that rare
/// pair, which the engine breaks by failing one side (review M1, listed for the stack-level security review).
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
        if (!schema.Indexes.Any(index => index.Exclude is not null) || dialect.TransactionLockStatement([Marker(0)], exclusive: true) is null)
        {
            return;
        }

        var keys = Keys(schema, rows).Order().ToList();
        if (keys.Count > 0)
        {
            var sql = dialect.TransactionLockStatement([.. keys.Select((_, i) => Marker(i))], exclusive: true)!;
            await ExecuteAsync(db, sql, keys, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The lock key one row takes on one exclusion: SHA-256 over the exclusion's name and the row's equality values, each in a
    /// spelling that does not depend on how the value was read — a create's candidate and an update's merged post-image of
    /// one resource give one key.
    /// </summary>
    /// <param name="schema">The entity being written.</param>
    /// <param name="index">An index of <paramref name="schema"/> declaring an exclusion.</param>
    /// <param name="row">The row, as a lookup of a column's value.</param>
    internal static long KeyFor(EntitySchema schema, IndexSchema index, Func<string, object?> row) =>
        Key(IndexNames.Exclusion(schema, index), DescriptorModelBuilder.UniqueColumns(schema, index.Fields), row);

    private static string Marker(int position) => $"@xk{position}";

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
                RelationalSqlBatch.AddParameter(command, Marker(i), keys[i]);
            }

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static IEnumerable<long> Keys(EntitySchema schema, IEnumerable<Func<string, object?>> rows) =>
        (from index in schema.Indexes.Where(index => index.Exclude is not null)
         from row in rows
         select KeyFor(schema, index, row)).Distinct();

    private static long Key(string name, IEnumerable<string> columns, Func<string, object?> row) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            name + "|" + string.Join("|", columns.Select(column => row(column) is { } value ? LockText(value) : string.Empty)))));

    private static string LockText(object value) => value switch
    {
        decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
        _ => SequenceFormat.KeyText(value),
    };
}
