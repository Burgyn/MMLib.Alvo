using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The relational <see cref="ITimerStore"/>: one connection per call, every write one transaction behind the database's
/// <see cref="RelationalConnectionFactory.WriteGate"/>, opened by a write — over <see cref="TimerTable"/>'s SQL and the
/// outbox's own insert, so a fire's fenced delete and its event commit together.
/// </summary>
/// <remarks>
/// <b>Internal</b>, like <see cref="EfCoreDeliveryStore"/>: the port is the contract, and a provider author implements that.
/// </remarks>
internal sealed partial class EfCoreTimerStore : ITimerStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly TimeProvider _time;
    private readonly Func<EfAlvoData> _data;
    private readonly string _timers;
    private readonly string _schedules;
    private readonly string _migrations;
    private readonly string _outbox;

    internal EfCoreTimerStore(RelationalConnectionFactory connections, AlvoOptions options, TimeProvider time, Func<EfAlvoData> data)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(data);

        _connections = connections;
        _time = time;
        _data = data;
        _timers = TimerTable.NameFor(options.SchemaPrefix);
        _schedules = ScheduleTable.NameFor(options.SchemaPrefix);
        _migrations = DataMigrationTable.NameFor(options.SchemaPrefix);
        _outbox = OutboxTable.NameFor(options.SchemaPrefix);
    }

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                await EnsureTablesAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureTablesAsync(DbConnection connection, CancellationToken ct)
    {
        await OutboxTable.EnsureAsync(connection, _outbox, ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, _timers, TimerTable.Ddl(_timers), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, TimerTable.DueIndexDdl(_timers), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, _schedules, ScheduleTable.Ddl(_schedules), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, _migrations, DataMigrationTable.Ddl(_migrations), ct).ConfigureAwait(false);
    }

    public Task<Guid> UpsertAsync(TimerEntry timer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var generation = Guid.CreateVersion7(_time.GetUtcNow());

        return JoinedWriteAsync(
            async (connection, transaction, ct) =>
            {
                await ExecuteAsync(connection, transaction, TimerTable.UpsertSql(_timers), command => BindTimer(command, timer, generation), ct)
                    .ConfigureAwait(false);
                return generation;
            },
            cancellationToken);
    }

    public Task<bool> CancelAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);

        return JoinedWriteAsync(
            async (connection, transaction, ct) =>
                await ExecuteAsync(connection, transaction, TimerTable.CancelSql(_timers), command => BindKey(command, scope, key), ct)
                    .ConfigureAwait(false) == 1,
            cancellationToken);
    }

    public Task<bool> CancelAsync(string scope, string key, Guid generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);

        return WriteAsync(
            async (connection, transaction, ct) =>
                await ExecuteAsync(connection, transaction, TimerTable.FireSql(_timers), command => BindFence(command, scope, key, generation), ct)
                    .ConfigureAwait(false) == 1,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ClaimedTimer>> ClaimTimersAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                var claimed = await RelationalSqlBatch.ReadWrittenRowsAsync(
                    connection, TimerTable.ClaimSql(_timers), command => BindClaim(command, batchSize, lease), ReadClaimed, cancellationToken)
                    .ConfigureAwait(false);

                return [.. claimed.OrderBy(timer => timer.DueAt).ThenBy(timer => timer.Scope, StringComparer.Ordinal).ThenBy(timer => timer.Key, StringComparer.Ordinal)];
            }
        }
    }

    public Task<bool> FireAsync(string scope, string key, Guid generation, AlvoCustomEvent customEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(customEvent);

        return InTransactionAsync(
            async (connection, transaction, ct) =>
                await ExecuteAsync(connection, transaction, TimerTable.FireSql(_timers), command => BindFence(command, scope, key, generation), ct).ConfigureAwait(false) == 1
                && await AppendedAsync(connection, transaction, [customEvent.Envelope], ct).ConfigureAwait(false),
            committed => committed,
            cancellationToken);
    }

    public async Task<TimerCounts> CountTimersAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync(
            TimerTable.CountSql(_timers),
            command => RelationalSqlBatch.AddParameter(command, "@now", Instant(_time.GetUtcNow())),
            reader => new TimerCounts { Pending = Count(reader, 0), Due = Count(reader, 1) },
            cancellationToken).ConfigureAwait(false);

        return rows.Single();
    }

    private static void BindTimer(DbCommand command, TimerEntry timer, Guid generation)
    {
        BindFence(command, timer.Scope, timer.Key, generation);
        RelationalSqlBatch.AddParameter(command, "@due_at", Instant(timer.DueAt));
        RelationalSqlBatch.AddParameter(command, "@message_type", timer.MessageType);
        RelationalSqlBatch.AddParameter(command, "@payload", timer.Payload);
        RelationalSqlBatch.AddParameter(command, "@provenance", timer.Provenance);
    }

    private static void BindFence(DbCommand command, string scope, string key, Guid generation)
    {
        BindKey(command, scope, key);
        RelationalSqlBatch.AddParameter(command, "@generation", generation.ToString());
    }

    private static void BindKey(DbCommand command, string scope, string key)
    {
        RelationalSqlBatch.AddParameter(command, "@scope", scope);
        RelationalSqlBatch.AddParameter(command, "@key", key);
    }

    private void BindClaim(DbCommand command, int batchSize, TimeSpan lease)
    {
        var now = _time.GetUtcNow();
        RelationalSqlBatch.AddParameter(command, "@now", Instant(now));
        RelationalSqlBatch.AddParameter(command, "@lease_until", Instant(now + lease));
        RelationalSqlBatch.AddParameter(command, "@batch", batchSize);
    }

    private static ClaimedTimer ReadClaimed(DbDataReader reader) => new()
    {
        Scope = reader.GetString(0),
        Key = reader.GetString(1),
        Generation = Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
        DueAt = InstantOf(reader.GetString(3)),
        MessageType = reader.GetString(4),
        Payload = reader.GetString(5),
        Provenance = reader.GetString(6),
    };

    private static int Count(DbDataReader reader, int ordinal) => Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset instant) => StoredInstant.Text(instant.ToUniversalTime());

    private static DateTimeOffset InstantOf(string text) => StoredInstant.Of(text);

    private async Task<bool> AppendedAsync(DbConnection connection, DbTransaction transaction, IEnumerable<AlvoEvent> events, CancellationToken ct)
    {
        foreach (var @event in events)
        {
            await OutboxTable.InsertAsync(connection, transaction, _outbox, @event, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// A timer write in the transaction unit open on the flow when there is one — so it commits or rolls back with the unit —
    /// and in a transaction of its own otherwise.
    /// </summary>
    private Task<T> JoinedWriteAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> write, CancellationToken ct) =>
        AlvoFlow.Unit is WriteUnit unit ? _data().InFlowUnitAsync(unit, write, ct) : WriteAsync(write, ct);

    private Task<T> WriteAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> write, CancellationToken ct) =>
        InTransactionAsync(write, _ => true, ct);

    /// <summary>
    /// One transaction behind the database's write gate, committed only when <paramref name="commits"/> says so — otherwise
    /// rolled back on dispose, so a fenced statement that matched nothing leaves nothing behind.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> work, Func<T, bool> commits, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
            {
                var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var result = await work(connection, transaction, ct).ConfigureAwait(false);
                    if (commits(result))
                    {
                        await transaction.CommitAsync(ct).ConfigureAwait(false);
                    }

                    return result;
                }
            }
        }
    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection, DbTransaction transaction, string sql, Action<DbCommand> bind, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            bind(command);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<List<T>> ReadAsync<T>(string sql, Action<DbCommand> bind, Func<DbDataReader, T> read, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);

            return await RelationalSqlBatch.ReadRowsAsync(connection, transaction: null, sql, bind, read, ct).ConfigureAwait(false);
        }
    }
}
