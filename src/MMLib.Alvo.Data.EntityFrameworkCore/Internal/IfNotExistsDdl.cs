using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Runs a framework table's or index's <c>CREATE … IF NOT EXISTS</c> so that "another connection created it a moment
/// ago" is the success <c>IF NOT EXISTS</c> was asked for — on every connection, in every process.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>CREATE TABLE IF NOT EXISTS</c> is not concurrency-safe on PostgreSQL, and this was measured, not assumed.</b>
/// The existence check and the catalog insert are not atomic, so two sessions creating one name at the same instant
/// leave the loser with <c>23505 duplicate key value violates unique constraint "pg_type_typname_nsp_index"</c> (or
/// <c>42P07</c>) rather than a quiet no-op. Three replicas cold-starting against one empty database did exactly that on
/// the boot path, and two first keyed writes did it on the data path (HX-09's stage-0 lookup ensures the idempotency
/// table outside any transaction and outside the retry loop).
/// </para>
/// <para>
/// <b>The recovery reads the outcome instead of the error.</b> No <c>SQLSTATE</c> and no <c>SqliteErrorCode</c> is
/// inspected, because a driver that decoded engine error numbers here would need a new branch for every engine Alvo
/// adds — and the two PostgreSQL codes a lost race produces are already two. A table is probed with a zero-row select;
/// an index is re-run once, since its table exists whatever the failure was. If the object is there afterwards the
/// intent was met by whoever created it; if not, the <em>first</em> failure is rethrown unchanged. That is the "re-read
/// rather than classify" discipline <see cref="VersionRowWriter"/> uses to tell a lost race from a genuine failure.
/// </para>
/// <para>
/// <b>Outside a transaction only.</b> On PostgreSQL a failed statement aborts the transaction it ran in, so the probe
/// would fail too and the original error would be rethrown — the safe direction, but no recovery. Every caller runs its
/// DDL autocommit, for the measured reason <see cref="IdempotencyTable.EnsureAsync"/> records.
/// </para>
/// <para>
/// One process serialises its own first ensure where it can (<see cref="OnceDdl"/>); this is what makes the
/// <em>other</em> processes of a deployment safe, which no in-process guard can.
/// </para>
/// </remarks>
internal static class IfNotExistsDdl
{
    /// <summary>Runs <paramref name="ddl"/>, succeeding when <paramref name="tableName"/> exists afterwards.</summary>
    /// <param name="connection">An open connection with no transaction in flight.</param>
    /// <param name="tableName">The table <paramref name="ddl"/> creates.</param>
    /// <param name="ddl">The <c>CREATE TABLE IF NOT EXISTS</c> statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal static async Task CreateTableAsync(DbConnection connection, string tableName, string ddl, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(connection, ddl, ct).ConfigureAwait(false);
        }
        catch (DbException)
        {
            if (!await SucceedsAsync(connection, $"SELECT 1 FROM {tableName} WHERE 1 = 0", ct).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>Runs <paramref name="ddl"/> and, when it fails, once more — the index-shaped twin of <see cref="CreateTableAsync"/>.</summary>
    /// <remarks>
    /// <b>Not the table probe.</b> An index is built on a table that is already there, so asking whether the table exists
    /// answers yes for every failure — a lock timeout on a large outbox, a full disk, a missing column — and the caller
    /// would go on without the index. After a lost race the rival's index is committed, so <c>IF NOT EXISTS</c> skips it
    /// and the second run succeeds; any other failure fails again, and the first error is rethrown.
    /// </remarks>
    /// <param name="connection">An open connection with no transaction in flight.</param>
    /// <param name="ddl">The <c>CREATE INDEX IF NOT EXISTS</c> statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal static async Task CreateIndexAsync(DbConnection connection, string ddl, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(connection, ddl, ct).ConfigureAwait(false);
        }
        catch (DbException)
        {
            if (!await SucceedsAsync(connection, ddl, ct).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private static async Task<bool> SucceedsAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(connection, sql, ct).ConfigureAwait(false);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private static Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct) =>
        RelationalSqlBatch.ExecuteAsync(connection, [sql], transaction: null, ct);
}
