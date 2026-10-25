using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Events;

using System.Data.Common;
using System.Globalization;

using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// One <see cref="EfCoreDeliveryStore"/> over a started database, the outbox it reads envelopes from, a hand-moved clock,
/// and the two-claimant race only PostgreSQL's leg runs. Linked into both driver test projects.
/// </summary>
internal sealed class DeliveryStoreWorld : IDeliveryStoreWorld
{
    private const string SchemaPrefix = "alvo";

    private static readonly DateTimeOffset _start = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _blockDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _blockPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly Func<DbConnection> _connect;
    private readonly WorldClock _clock = new(_start);
    private readonly string _outbox = OutboxTable.NameFor(SchemaPrefix);
    private readonly string _table = DeliveryTable.NameFor(SchemaPrefix);
    private readonly string _attempts = DeliveryTable.AttemptsNameFor(SchemaPrefix);

    private DeliveryStoreWorld(Func<DbConnection> connect, IAlvoSqlDialect dialect, IRelationalWriteGate? writeGate)
    {
        _connect = connect;
        Store = new EfCoreDeliveryStore(
            new RelationalConnectionFactory(connect, writeGate), dialect, new AlvoOptions { SchemaPrefix = SchemaPrefix }, _clock);
    }

    public IDeliveryStore Store { get; }

    internal static async Task<DeliveryStoreWorld> StartAsync(Func<DbConnection> connect, IAlvoSqlDialect dialect, IRelationalWriteGate? writeGate = null)
    {
        var world = new DeliveryStoreWorld(connect, dialect, writeGate);
        await world.Store.EnsureAsync(Ct);

        return world;
    }

    public void Advance(TimeSpan duration) => _clock.Advance(duration);

    public async Task<Guid> AppendEventAsync(string partitionKey)
    {
        var id = await AppendUnfannedEventAsync(partitionKey);
        await MarkDispatchedAsync(id);

        return id;
    }

    public async Task<Guid> AppendUnfannedEventAsync(string partitionKey, int attempts = 0, bool claimed = false)
    {
        var id = AlvoEventId.Create(_clock.GetUtcNow());
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            await OutboxTable.InsertAsync(connection, transaction: null, _outbox, EventWith(id, partitionKey), Ct);
            await ExecuteAsync(connection, $"UPDATE {_outbox} SET attempts = @value WHERE id = @id", id, attempts);
            if (claimed)
            {
                await ExecuteAsync(connection, $"UPDATE {_outbox} SET claimed_by = @value WHERE id = @id", id, "dead-dispatcher");
                await ExecuteAsync(connection, $"UPDATE {_outbox} SET claimed_at = @value WHERE id = @id", id, StoredInstant.Text(_clock.GetUtcNow()));
            }
        }

        return id;
    }

    public async Task MarkDispatchedAsync(Guid eventId)
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            await ExecuteAsync(connection, $"UPDATE {_outbox} SET dispatched_at = @value WHERE id = @id", eventId, StoredInstant.Text(_clock.GetUtcNow()));
        }
    }

    public async Task<IReadOnlyList<DeliveryOperationEntry>> OperationsAsync()
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);

            return await RelationalSqlBatch.ReadRowsAsync(
                connection, transaction: null, DeliveryOperationsTable.AllOperationsSql(DeliveryOperationsTable.LogNameFor(SchemaPrefix)),
                _ => { }, EfCoreDeliveryStore.ReadEntry, Ct);
        }
    }

    /// <summary>Puts a delivery back to pending with its claim count at zero, as an operator's redeliver does.</summary>
    internal async Task ResetAsync(Guid delivery)
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            await ExecuteAsync(
                connection,
                $"UPDATE {_table} SET status = 'pending', attempts = @value, claimed_by = NULL, lease_until = NULL WHERE id = @id",
                delivery,
                0);
        }
    }

    /// <summary>The attempt numbers the history holds for one delivery, ascending.</summary>
    internal async Task<IReadOnlyList<int>> AttemptNumbersAsync(Guid delivery)
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);

            return await RelationalSqlBatch.ReadRowsAsync(
                connection,
                transaction: null,
                $"SELECT attempt FROM {_attempts} WHERE delivery_id = @id ORDER BY attempt",
                command => RelationalSqlBatch.AddParameter(command, "@id", delivery.ToString()),
                reader => Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                Ct);
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, Guid id, object value)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            RelationalSqlBatch.AddParameter(command, "@value", value);
            RelationalSqlBatch.AddParameter(command, "@id", id.ToString());
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    /// <summary>
    /// Two claimants racing one lane head with no <c>SKIP LOCKED</c>: A claims inside an open transaction, B's claim
    /// blocks on A's row lock, A commits, and B — which re-checks only the outer condition — must then claim nothing.
    /// PostgreSQL only: A commits only once a third connection sees B's backend waiting on a lock, so the race is
    /// constructed every run rather than whenever B happens to reach the row in time.
    /// </summary>
    internal async Task<(int First, int Second)> TwoConcurrentClaimsAsync(string subscriber)
    {
        var winner = _connect();
        await using (winner.ConfigureAwait(false))
        {
            var loser = _connect();
            await using (loser.ConfigureAwait(false))
            {
                var observer = _connect();
                await using (observer.ConfigureAwait(false))
                {
                    await winner.OpenAsync(Ct);
                    await loser.OpenAsync(Ct);
                    await observer.OpenAsync(Ct);

                    return await RaceAsync(winner, loser, observer, subscriber);
                }
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> while another connection holds <paramref name="subscriber"/>'s advisory lock — the
    /// exclusive or the shared form — and lets go only once a third connection sees a backend of this database waiting on
    /// an advisory lock, so the action provably took the subscriber's lock in the conflicting mode. PostgreSQL only.
    /// </summary>
    internal async Task<T> WhileSubscriberLockedAsync<T>(string subscriber, bool exclusive, Func<Task<T>> action)
    {
        var holder = _connect();
        await using (holder.ConfigureAwait(false))
        {
            var observer = _connect();
            await using (observer.ConfigureAwait(false))
            {
                await holder.OpenAsync(Ct);
                await observer.OpenAsync(Ct);
                var holding = await holder.BeginTransactionAsync(Ct);
                await using (holding.ConfigureAwait(false))
                {
                    await LockSubscriberOnAsync(holder, holding, subscriber, exclusive);
                    var waiting = action();
                    await WaitUntilAdvisoryWaitAsync(observer, waiting);
                    await holding.CommitAsync(Ct);

                    return await waiting;
                }
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task LockSubscriberOnAsync(DbConnection connection, DbTransaction transaction, string subscriber, bool exclusive)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = exclusive ? "SELECT pg_advisory_xact_lock(@key)" : "SELECT pg_advisory_xact_lock_shared(@key)";
            RelationalSqlBatch.AddParameter(command, "@key", DeliveryOperationsTable.SubscriberLockKey(_table, subscriber));
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private static async Task WaitUntilAdvisoryWaitAsync(DbConnection observer, Task waiting)
    {
        const string Waiters =
            "SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())";
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Convert.ToInt64(await ScalarAsync(observer, Waiters, backend: null), CultureInfo.InvariantCulture) == 0)
        {
            if (waiting.IsCompleted || waited.Elapsed > _blockDeadline)
            {
                throw new InvalidOperationException("The action never waited on the subscriber's advisory lock; it does not take it.");
            }

            await Task.Delay(_blockPollInterval, Ct);
        }
    }

    /// <summary>
    /// The engine's plan for the claim of one ordered subscriber, every row and column of <paramref name="explain"/>'s
    /// output joined into one text, after <paramref name="setup"/> runs on the same connection.
    /// </summary>
    internal async Task<string> ExplainClaimAsync(string explain, params string[] setup)
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            foreach (var statement in setup)
            {
                await ScalarOrNothingAsync(connection, statement);
            }

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = $"{explain} {DeliveryTable.ClaimSql(_table, _outbox, ordered: 1, unordered: 0)}";
                BindClaim(command, "explain", "send-confirmation");

                return await PlanTextAsync(command);
            }
        }
    }

    /// <summary>The engine's plan for a page of one subscriber continuing after a cursor, as one text.</summary>
    internal async Task<string> ExplainSubscriberPageAsync(string explain)
    {
        var filter = new DeliveryFilter { Subscriber = "send-confirmation", After = Guid.NewGuid(), Limit = 50 };
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = $"{explain} {DeliveryOperationsTable.ListSql(_table, filter)}";
                RelationalSqlBatch.AddParameter(command, "@subscriber", filter.Subscriber);
                RelationalSqlBatch.AddParameter(command, "@after", filter.After.ToString()!);
                RelationalSqlBatch.AddParameter(command, "@limit", filter.Limit);

                return await PlanTextAsync(command);
            }
        }
    }

    private static async Task ScalarOrNothingAsync(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private static async Task<string> PlanTextAsync(DbCommand command)
    {
        var lines = new List<string>();
        var reader = await command.ExecuteReaderAsync(Ct);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(Ct))
            {
                lines.Add(string.Join(" ", Enumerable.Range(0, reader.FieldCount).Select(column => Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture))));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<(int First, int Second)> RaceAsync(DbConnection winner, DbConnection loser, DbConnection observer, string subscriber)
    {
        var loserBackend = await ScalarAsync(loser, "SELECT pg_backend_pid()", backend: null);
        var winning = await winner.BeginTransactionAsync(Ct);
        await using (winning.ConfigureAwait(false))
        {
            var first = await ClaimOnAsync(winner, winning, subscriber);
            var losing = await loser.BeginTransactionAsync(Ct);
            await using (losing.ConfigureAwait(false))
            {
                var blocked = ClaimOnAsync(loser, losing, subscriber);
                await WaitUntilBlockedAsync(observer, loserBackend, blocked);
                await winning.CommitAsync(Ct);
                var second = await blocked;
                await losing.CommitAsync(Ct);

                return (first, second);
            }
        }
    }

    private static async Task WaitUntilBlockedAsync(DbConnection observer, object backend, Task blocked)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Convert.ToInt64(await ScalarAsync(observer, "SELECT COUNT(*) FROM pg_stat_activity WHERE pid = @backend AND wait_event_type = 'Lock'", backend), CultureInfo.InvariantCulture) == 0)
        {
            if (blocked.IsCompleted || waited.Elapsed > _blockDeadline)
            {
                throw new InvalidOperationException("The second claimant never waited on the first's row lock; no race was constructed.");
            }

            await Task.Delay(_blockPollInterval, Ct);
        }
    }

    private static async Task<object> ScalarAsync(DbConnection connection, string sql, object? backend)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            if (backend is not null)
            {
                RelationalSqlBatch.AddParameter(command, "@backend", backend);
            }

            return await command.ExecuteScalarAsync(Ct) ?? throw new InvalidOperationException($"No value from: {sql}");
        }
    }

    private async Task<int> ClaimOnAsync(DbConnection connection, DbTransaction transaction, string subscriber)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = DeliveryTable.ClaimSql(_table, _outbox, ordered: 1, unordered: 0);
            BindClaim(command, "racer", subscriber);

            return await CountRowsAsync(command);
        }
    }

    private void BindClaim(DbCommand command, string claimant, string subscriber)
    {
        RelationalSqlBatch.AddParameter(command, "@now", StoredInstant.Text(_clock.GetUtcNow()));
        RelationalSqlBatch.AddParameter(command, "@lease_until", StoredInstant.Text(_clock.GetUtcNow() + TimeSpan.FromMinutes(1)));
        RelationalSqlBatch.AddParameter(command, "@claimed_by", claimant);
        RelationalSqlBatch.AddParameter(command, "@batch", 10);
        RelationalSqlBatch.AddParameter(command, "@outbox_max_attempts", 10);
        RelationalSqlBatch.AddParameter(command, "@outbox_stale_before", StoredInstant.Text(_clock.GetUtcNow() - TimeSpan.FromMinutes(5)));
        RelationalSqlBatch.AddParameter(command, "@s0", subscriber);
    }

    private static async Task<int> CountRowsAsync(DbCommand command)
    {
        var reader = await command.ExecuteReaderAsync(Ct);
        await using (reader.ConfigureAwait(false))
        {
            var rows = 0;
            while (await reader.ReadAsync(Ct))
            {
                rows++;
            }

            return rows;
        }
    }

    private AlvoEvent EventWith(Guid id, string partitionKey) => new()
    {
        Id = id,
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.updated",
        Time = _clock.GetUtcNow(),
        Subject = partitionKey.Replace(':', '/'),
        PartitionKey = partitionKey,
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = id.ToString("N", CultureInfo.InvariantCulture),
        Data = new AlvoEventData { Record = new(new Dictionary<string, object?>(StringComparer.Ordinal)) },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class WorldClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
