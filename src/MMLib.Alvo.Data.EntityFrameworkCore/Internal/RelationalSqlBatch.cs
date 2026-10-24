using System.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Runs an ordered list of SQL commands, either in a caller's transaction or in its own, reads the rows a statement
/// returns, and binds parameters — the driver's shared raw-SQL plumbing.
/// </summary>
internal static class RelationalSqlBatch
{
    /// <summary>Opens <paramref name="connection"/>, runs <paramref name="sql"/> in one transaction, and commits.</summary>
    public static async Task ExecuteAsync(DbConnection connection, IReadOnlyList<string> sql, CancellationToken ct)
    {
        await OpenAsync(connection, ct).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await ExecuteAsync(connection, sql, transaction, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens <paramref name="connection"/> and runs <paramref name="sql"/> with <b>no transaction at all</b>,
    /// each statement committing on its own.
    /// </summary>
    /// <remarks>
    /// For the statements an engine only honours outside a transaction — see
    /// <see cref="IAlvoSqlDialect.MigrationFraming"/>, whose whole existence is that SQLite's
    /// <c>PRAGMA foreign_keys</c> is a documented no-op inside one. Deliberately not "in the caller's
    /// transaction if there is one": a statement that is a no-op inside a transaction has to be run outside
    /// one or not at all, and running it inside would look like it worked.
    /// </remarks>
    public static async Task ExecuteUntransactedAsync(DbConnection connection, IReadOnlyList<string> sql, CancellationToken ct)
    {
        if (sql.Count == 0)
        {
            return;
        }

        await OpenAsync(connection, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, sql, transaction: null, ct).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="sql"/> against an already-open connection within <paramref name="transaction"/>.</summary>
    public static async Task ExecuteAsync(DbConnection connection, IReadOnlyList<string> sql, DbTransaction? transaction, CancellationToken ct)
    {
        foreach (var commandText in sql)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                continue;
            }

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = commandText;
                command.Transaction = transaction;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Opens <paramref name="connection"/> if it is not already open.</summary>
    public static async Task OpenAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one writing statement that returns rows — an <c>UPDATE … RETURNING</c> claim — inside a transaction of its own
    /// on the open <paramref name="connection"/>, reads every row, and commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The transaction is what makes a busy SQLite file a wait rather than a failure.</b> In autocommit, SQLite commits
    /// such a statement on its <em>last</em> step, which <c>Microsoft.Data.Sqlite</c> reaches through
    /// <c>DbDataReader.Read</c> — and it retries a busy lock only on a statement's first step, so a commit that met a
    /// concurrent reader threw <c>database is locked</c> at once. The provider's own default transaction is
    /// <c>BEGIN IMMEDIATE</c> on SQLite, taking the write lock up front through the retried path, and its <c>COMMIT</c>
    /// is retried too; on PostgreSQL it is a plain read-committed transaction around one statement, which changes
    /// nothing. Measured as the 8-thread claim fact of <c>SqliteDeliveryStoreTests</c> failing under a loaded machine.
    /// </para>
    /// <para>
    /// <b>Write-first, so it is not the shape spike Q5 refused.</b> The statement is the transaction's first and only
    /// one; no read precedes the write, which is the shape <see cref="MMLib.Alvo.Events.IOutboxStore"/> permits.
    /// </para>
    /// </remarks>
    public static async Task<List<T>> ReadWrittenRowsAsync<T>(
        DbConnection connection, string sql, Action<DbCommand> bind, Func<DbDataReader, T> read, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var results = await ReadRowsAsync(connection, transaction, sql, bind, read, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return results;
        }
    }

    /// <summary>Runs <paramref name="sql"/> on the open <paramref name="connection"/> and maps every row it returns.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction to run in, or <see langword="null"/> for autocommit.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="bind">Binds its parameters.</param>
    /// <param name="read">Maps one row.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task<List<T>> ReadRowsAsync<T>(
        DbConnection connection, DbTransaction? transaction, string sql, Action<DbCommand> bind, Func<DbDataReader, T> read, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            bind(command);
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var results = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    results.Add(read(reader));
                }

                return results;
            }
        }
    }

    /// <summary>Adds a named, valued parameter to <paramref name="command"/>.</summary>
    /// <remarks>
    /// Shared by <see cref="EfCoreDescriptorVersionStore"/> and <see cref="VersionRowWriter"/> so
    /// both bind their SQL parameters identically instead of each keeping its own copy.
    /// </remarks>
    public static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
