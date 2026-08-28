using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The relational <see cref="IOutboxStore"/>: one connection and <b>one statement</b> per call, over the
/// engine-agnostic SQL in <see cref="OutboxTable"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One statement per member is a measured constraint, not a style.</b> Spike Q5 found exactly one shape
/// that breaks a dispatcher sharing a SQLite database with a live request path: a transaction that
/// <em>reads</em> and then <em>writes</em>. Under WAL that fails the dispatcher unretryably with
/// <c>SQLITE_BUSY_SNAPSHOT</c> after burning the whole 30-second retry loop, and in the shipped journal mode it
/// fails the request path instead — and <c>journal_mode=WAL</c> is persistent in the database file, so it is
/// not a redeploy away from being undone. Every other shape measured waited (~1 s) and then succeeded, in both
/// directions, because <c>Microsoft.Data.Sqlite</c>'s <c>DefaultTimeout</c> is 30 s and its retry loop covers
/// <c>BEGIN</c>. So each member here opens a connection, issues one autocommit statement, and disposes — which
/// satisfies the constraint by construction. Wrapping two of them in a transaction to be tidy is the edit that
/// would undo it.
/// </para>
/// <para>
/// <b>Nothing here sets a <c>Default Timeout</c>, a <c>busy_timeout</c> or a <c>journal_mode</c></b>, on its
/// own connection string or anywhere else. The shipped registration was already correct for the reason above,
/// and an explicit <c>PRAGMA busy_timeout=5000</c> changed nothing measurable.
/// </para>
/// <para>
/// <b>Public, with an internal constructor</b>, exactly as <see cref="EfCoreSchemaMigrator"/> is: the type is
/// part of what a host can see resolve for <see cref="IOutboxStore"/>, while what it is built from —
/// <see cref="RelationalConnectionFactory"/> — stays the driver's own business.
/// </para>
/// </remarks>
public sealed class EfCoreOutboxStore : IOutboxStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly TimeProvider _time;
    private readonly string _tableName;

    /// <summary>Initializes a new store over one database's outbox table.</summary>
    /// <param name="connections">Creates a fresh ADO.NET connection per call; each is owned and disposed within that call.</param>
    /// <param name="options">Supplies the validated <see cref="AlvoOptions.SchemaPrefix"/> the table is named from.</param>
    /// <param name="time">The clock a claim stamps and measures a lease against.</param>
    internal EfCoreOutboxStore(
        RelationalConnectionFactory connections, AlvoOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _connections = connections;
        _time = time;
        _tableName = OutboxTable.NameFor(options.SchemaPrefix);
    }

    /// <inheritdoc/>
    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            await OutboxTable.EnsureAsync(connection, _tableName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The returned batch is sorted here rather than trusted from the engine: <c>RETURNING</c>'s row order is
    /// arbitrary on both shipped engines in measured fact (spike Q3), so the statement's <c>ORDER BY</c>
    /// decides <em>which</em> entries are claimed and this sort decides the order they are delivered in.
    /// Ordinally over the id's text, which is the collation both engines agree with (Q2) and therefore the
    /// order a later claim would read them back in.
    /// </remarks>
    public async Task<IReadOnlyList<OutboxEntry>> ClaimAsync(
        string claimant,
        int batchSize,
        int maxAttempts,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimant);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lease.Ticks);

        var claimed = await ClaimBatchAsync(claimant, batchSize, maxAttempts, lease, cancellationToken)
            .ConfigureAwait(false);

        return [.. claimed.OrderBy(entry => entry.Id.ToString(), StringComparer.Ordinal)];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>The same INSERT a data event travels on, with no transaction under it.</b> The statement is
    /// <see cref="OutboxTable.InsertAsync"/>'s, so the column list, the payload encoding and the initial
    /// attempt count have one authority whichever path appended the entry; only the transaction differs, and
    /// it differs because a custom application event has no data change to be atomic with.
    /// </para>
    /// <para>
    /// <b>No name check here, deliberately.</b> The parameter type is what holds the reserved-namespace
    /// guarantee (<see cref="AlvoCustomEvent"/>); a second check in this driver would be a second authority on
    /// which namespaces are reserved, and the next driver would be free to omit it.
    /// </para>
    /// </remarks>
    public async Task AppendAsync(AlvoCustomEvent customEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customEvent);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            await OutboxTable
                .InsertAsync(connection, transaction: null, _tableName, customEvent.Envelope, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task MarkDispatchedAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            OutboxTable.MarkDispatchedSql(_tableName),
            command => RelationalSqlBatch.AddParameter(
                command, "@dispatched_at", StoredInstant.Text(_time.GetUtcNow())),
            id,
            cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="retryAfter"/> is stamped into <c>claimed_at</c> while <c>claimed_by</c> is cleared, which
    /// is the released state <see cref="OutboxTable.ClaimSql"/> compares against the current instant rather than
    /// against a lease. <see cref="TimeSpan.Zero"/> therefore stamps the present and the entry is claimable at
    /// once, which is what the port promises for it.
    /// </remarks>
    public Task ReleaseAsync(Guid id, TimeSpan retryAfter, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryAfter.Ticks);

        return ExecuteAsync(
            OutboxTable.ReleaseSql(_tableName),
            command => RelationalSqlBatch.AddParameter(
                command, "@claimed_at", StoredInstant.Text(_time.GetUtcNow() + retryAfter)),
            id,
            cancellationToken);
    }

    private async Task<IReadOnlyList<OutboxEntry>> ClaimBatchAsync(
        string claimant, int batchSize, int maxAttempts, TimeSpan lease, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = OutboxTable.ClaimSql(_tableName);
                RelationalSqlBatch.AddParameter(command, "@claimed_at", StoredInstant.Text(now));
                RelationalSqlBatch.AddParameter(command, "@claimed_by", claimant);
                RelationalSqlBatch.AddParameter(command, "@now", StoredInstant.Text(now));
                RelationalSqlBatch.AddParameter(command, "@stale_before", StoredInstant.Text(now - lease));
                RelationalSqlBatch.AddParameter(command, "@max_attempts", maxAttempts);
                RelationalSqlBatch.AddParameter(command, "@batch", batchSize);

                return await ReadEntriesAsync(command, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IReadOnlyList<OutboxEntry>> ReadEntriesAsync(DbCommand command, CancellationToken ct)
    {
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var entries = new List<OutboxEntry>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }
    }

    private static OutboxEntry ReadEntry(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(IdColumn)),
        reader.GetString(EventTypeColumn),
        reader.GetString(PartitionKeyColumn),
        reader.GetString(PayloadColumn),
        reader.GetInt32(AttemptsColumn));

    private async Task ExecuteAsync(
        string sql, Action<DbCommand> bind, Guid id, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = sql;
                bind(command);
                RelationalSqlBatch.AddParameter(command, "@id", id.ToString());

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private const int IdColumn = 0;
    private const int EventTypeColumn = 1;
    private const int PartitionKeyColumn = 2;
    private const int PayloadColumn = 3;
    private const int AttemptsColumn = 4;
}
