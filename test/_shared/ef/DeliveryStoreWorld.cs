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

    private DeliveryStoreWorld(Func<DbConnection> connect, IRelationalWriteGate? writeGate)
    {
        _connect = connect;
        Store = new EfCoreDeliveryStore(new RelationalConnectionFactory(connect, writeGate), new AlvoOptions { SchemaPrefix = SchemaPrefix }, _clock);
    }

    public IDeliveryStore Store { get; }

    internal static async Task<DeliveryStoreWorld> StartAsync(Func<DbConnection> connect, IRelationalWriteGate? writeGate = null)
    {
        var world = new DeliveryStoreWorld(connect, writeGate);
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
            RelationalSqlBatch.AddParameter(command, "@now", StoredInstant.Text(_clock.GetUtcNow()));
            RelationalSqlBatch.AddParameter(command, "@lease_until", StoredInstant.Text(_clock.GetUtcNow() + TimeSpan.FromMinutes(1)));
            RelationalSqlBatch.AddParameter(command, "@claimed_by", "racer");
            RelationalSqlBatch.AddParameter(command, "@batch", 10);
            RelationalSqlBatch.AddParameter(command, "@outbox_max_attempts", 10);
            RelationalSqlBatch.AddParameter(command, "@outbox_stale_before", StoredInstant.Text(_clock.GetUtcNow() - TimeSpan.FromMinutes(5)));
            RelationalSqlBatch.AddParameter(command, "@s0", subscriber);

            return await CountRowsAsync(command);
        }
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
