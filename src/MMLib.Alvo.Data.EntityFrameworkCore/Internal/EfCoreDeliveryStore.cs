using MMLib.Alvo.Events;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The relational <see cref="IDeliveryStore"/>: one connection per call, one statement — or a write-first transaction: the
/// claim's one <c>UPDATE … RETURNING</c> (<see cref="RelationalSqlBatch.ReadWrittenRowsAsync"/>), or, to answer for an
/// attempt, the fenced update then the attempt row — over <see cref="DeliveryTable"/>'s SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every write waits its turn at the database's <see cref="RelationalConnectionFactory.WriteGate"/> first</b> — the
/// claim, each answer, the fan-out, the end of a retired subscriber's work and the table set-up; the reads do not. On
/// SQLite that turns the dispatcher's concurrent answers into a queue instead of busy locks the provider sleeps through.
/// </para>
/// <para>
/// <b>A claim and an operator's reset of one subscriber are serialized by a subscriber lock</b>
/// (<see cref="IAlvoSqlDialect.TransactionLockStatement"/>): the claim takes each subscriber it may claim for shared, a
/// redeliver takes its delivery's subscriber exclusively, each as the first statement of its transaction. On PostgreSQL
/// under READ COMMITTED a lane check is evaluated against the statement's own snapshot and is not re-checked when a
/// concurrent writer commits, so without the lock a redeliver and a claim of the same lane — ordered or not — could each
/// pass the other's check: a redelivered earlier delivery and a later one both claimable, or two deliveries of one record
/// running. SQLite needs no lock (the write gate admits one writer at a time), and its dialect answers none. The lock
/// statement reads no row, so a transaction that opens with it still writes before it reads anything.
/// </para>
/// <para>
/// <b>Internal</b>: unlike <see cref="EfCoreOutboxStore"/> it is not part of what a host resolves by type; the port is the
/// contract, and a provider author implements that.
/// </para>
/// </remarks>
internal sealed partial class EfCoreDeliveryStore : IDeliveryStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly IAlvoSqlDialect _dialect;
    private readonly TimeProvider _time;
    private readonly string _table;
    private readonly string _attempts;
    private readonly string _outbox;
    private readonly string _controls;
    private readonly string _log;

    internal EfCoreDeliveryStore(RelationalConnectionFactory connections, IAlvoSqlDialect dialect, AlvoOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _connections = connections;
        _dialect = dialect;
        _time = time;
        _table = DeliveryTable.NameFor(options.SchemaPrefix);
        _attempts = DeliveryTable.AttemptsNameFor(options.SchemaPrefix);
        _outbox = OutboxTable.NameFor(options.SchemaPrefix);
        _controls = DeliveryOperationsTable.ControlsNameFor(options.SchemaPrefix);
        _log = DeliveryOperationsTable.LogNameFor(options.SchemaPrefix);
    }

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                await OutboxTable.EnsureAsync(connection, _outbox, cancellationToken).ConfigureAwait(false);
                await DeliveryTable.EnsureAsync(connection, _table, _attempts, _outbox, cancellationToken).ConfigureAwait(false);
                await RelationalSqlBatch.ExecuteAsync(
                    connection, DeliveryOperationsTable.Statements(_table, _controls, _log), transaction: null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        return requests.Count == 0
            ? Task.FromResult(0)
            : ExecuteAsync(DeliveryTable.FanOutSql(_table, requests.Count), command => BindRequests(command, requests), cancellationToken);
    }

    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(claim.BatchSize);
        if (claim.Ordered.Count + claim.Unordered.Count == 0)
        {
            return [];
        }

        var claimed = await ClaimRowsAsync(claim, cancellationToken).ConfigureAwait(false);
        var payloads = await PayloadsAsync([.. claimed.Select(delivery => delivery.EventId).Distinct()], cancellationToken).ConfigureAwait(false);

        return [.. claimed
            .Select(delivery => delivery with { Payload = payloads.GetValueOrDefault(delivery.EventId) })
            .OrderBy(delivery => delivery.EventId.ToString(), StringComparer.Ordinal)
            .ThenBy(delivery => delivery.Subscriber, StringComparer.Ordinal)];
    }

    public Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        return AnswerAsync(DeliveryTable.CompleteSql(_table), _ => { }, attempt, DeliveryTable.Succeeded, errorType: null, cancellationToken);
    }

    public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(failure);
        var dead = failure.RetryAfter is null || attempt.Delivery.Attempts >= failure.MaxAttempts;

        return AnswerAsync(
            DeliveryTable.FailSql(_table),
            command => BindFailure(command, failure),
            attempt,
            dead ? DeliveryTable.Dead : DeliveryTable.FailedOutcome,
            failure.ErrorType,
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default)
    {
        return await ReadAsync(DeliveryTable.PendingSubscribersSql(_table), _ => { }, reader => reader.GetString(0), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
        EndPendingAsync(subscriber, DeliveryTable.Dead, reason, cancellationToken);

    public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
        EndPendingAsync(subscriber, DeliveryTable.Cancelled, "retired", cancellationToken);

    private Task<int> EndPendingAsync(string subscriber, string status, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return ExecuteAsync(DeliveryTable.EndPendingSql(_table), command =>
        {
            RelationalSqlBatch.AddParameter(command, "@status", status);
            RelationalSqlBatch.AddParameter(command, "@reason", reason);
            RelationalSqlBatch.AddParameter(command, "@now", Now);
            RelationalSqlBatch.AddParameter(command, "@subscriber", subscriber);
        }, ct);
    }

    private async Task<List<ClaimedDelivery>> ClaimRowsAsync(DeliveryClaim claim, CancellationToken ct)
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
                    await LockSubscribersAsync(connection, transaction, [.. claim.Ordered, .. claim.Unordered], exclusive: false, ct).ConfigureAwait(false);
                    var claimed = await RelationalSqlBatch.ReadRowsAsync(
                        connection,
                        transaction,
                        DeliveryTable.ClaimSql(_table, _outbox, claim.Ordered.Count, claim.Unordered.Count),
                        command => BindClaim(command, claim),
                        ReadClaimed,
                        ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    return claimed;
                }
            }
        }
    }

    /// <summary>
    /// Takes the subscribers' transaction locks, shared or exclusive, in ascending key order — the statement the dialect
    /// answers, or nothing on an engine with one writer — so the transaction's next statement starts after any conflicting
    /// claim or reset of those subscribers committed.
    /// </summary>
    private async Task LockSubscribersAsync(
        DbConnection connection, DbTransaction transaction, IEnumerable<string> subscribers, bool exclusive, CancellationToken ct)
    {
        var keys = subscribers.Distinct(StringComparer.Ordinal).Select(subscriber => DeliveryOperationsTable.SubscriberLockKey(_table, subscriber)).Distinct().Order().ToList();
        if (keys.Count == 0 || _dialect.TransactionLockStatement([.. keys.Select((_, index) => $"@lock{index}")], exclusive) is not { } sql)
        {
            return;
        }

        await ExecuteInAsync(connection, transaction, sql, command =>
        {
            for (var index = 0; index < keys.Count; index++)
            {
                RelationalSqlBatch.AddParameter(command, $"@lock{index}", keys[index]);
            }
        }, ct).ConfigureAwait(false);
    }

    private void BindClaim(DbCommand command, DeliveryClaim claim)
    {
        var now = _time.GetUtcNow();
        RelationalSqlBatch.AddParameter(command, "@now", StoredInstant.Text(now));
        RelationalSqlBatch.AddParameter(command, "@lease_until", StoredInstant.Text(now + claim.Lease));
        RelationalSqlBatch.AddParameter(command, "@claimed_by", claim.Claimant);
        RelationalSqlBatch.AddParameter(command, "@batch", claim.BatchSize);
        RelationalSqlBatch.AddParameter(command, "@outbox_max_attempts", claim.OutboxMaxAttempts);
        RelationalSqlBatch.AddParameter(command, "@outbox_stale_before", StoredInstant.Text(now - claim.OutboxClaimLease));
        var index = 0;
        foreach (var subscriber in claim.Ordered.Concat(claim.Unordered))
        {
            RelationalSqlBatch.AddParameter(command, $"@s{index++}", subscriber);
        }
    }

    private static ClaimedDelivery ReadClaimed(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        EventId = Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        Subscriber = reader.GetString(2),
        PartitionKey = reader.GetString(3),
        Attempts = reader.GetInt32(4),
    };

    private async Task<Dictionary<Guid, string>> PayloadsAsync(IReadOnlyList<Guid> events, CancellationToken ct)
    {
        if (events.Count == 0)
        {
            return [];
        }

        var payloads = await ReadAsync(
            DeliveryTable.PayloadsSql(_outbox, events.Count),
            command => BindEvents(command, events),
            reader => (Id: Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture), Payload: reader.GetString(1)),
            ct).ConfigureAwait(false);

        return payloads.ToDictionary(payload => payload.Id, payload => payload.Payload);
    }

    private static void BindEvents(DbCommand command, IReadOnlyList<Guid> events)
    {
        for (var index = 0; index < events.Count; index++)
        {
            RelationalSqlBatch.AddParameter(command, $"@e{index}", events[index].ToString());
        }
    }

    private void BindRequests(DbCommand command, IReadOnlyList<DeliveryRequest> requests)
    {
        RelationalSqlBatch.AddParameter(command, "@now", Now);
        for (var row = 0; row < requests.Count; row++)
        {
            BindRequest(command, row, requests[row]);
        }
    }

    private void BindRequest(DbCommand command, int row, DeliveryRequest request)
    {
        var dead = request.DeadReason is not null;
        RelationalSqlBatch.AddParameter(command, $"@id{row}", AlvoEventId.Create(_time.GetUtcNow()).ToString());
        RelationalSqlBatch.AddParameter(command, $"@event_id{row}", request.EventId.ToString());
        RelationalSqlBatch.AddParameter(command, $"@subscriber{row}", request.Subscriber);
        RelationalSqlBatch.AddParameter(command, $"@partition_key{row}", request.PartitionKey);
        RelationalSqlBatch.AddParameter(command, $"@tenant_id{row}", (object?)request.TenantId ?? DBNull.Value);
        RelationalSqlBatch.AddParameter(command, $"@kind{row}", request.Kind);
        RelationalSqlBatch.AddParameter(command, $"@status{row}", dead ? DeliveryTable.Dead : DeliveryTable.Pending);
        RelationalSqlBatch.AddParameter(command, $"@completed_at{row}", dead ? Now : DBNull.Value as object);
        RelationalSqlBatch.AddParameter(command, $"@last_error_type{row}", (object?)request.DeadReason ?? DBNull.Value);
    }

    private void BindFailure(DbCommand command, DeliveryFailure failure)
    {
        RelationalSqlBatch.AddParameter(command, "@retryable", failure.RetryAfter is null ? 0 : 1);
        RelationalSqlBatch.AddParameter(command, "@max_attempts", failure.MaxAttempts);
        RelationalSqlBatch.AddParameter(command, "@not_before", StoredInstant.Text(_time.GetUtcNow() + (failure.RetryAfter ?? TimeSpan.Zero)));
        RelationalSqlBatch.AddParameter(command, "@error_type", failure.ErrorType);
    }

    private async Task<bool> AnswerAsync(
        string sql, Action<DbCommand> bind, DeliveryAttempt attempt, string outcome, string? errorType, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
            {
                return await AnswerInTransactionAsync(connection, sql, bind, attempt, outcome, errorType, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> AnswerInTransactionAsync(
        DbConnection connection, string sql, Action<DbCommand> bind, DeliveryAttempt attempt, string outcome, string? errorType, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var held = await FencedUpdateAsync(connection, transaction, sql, bind, attempt, ct).ConfigureAwait(false) == 1;
            if (held)
            {
                await InsertAttemptAsync(connection, transaction, attempt, outcome, errorType, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return held;
        }
    }

    private async Task<int> FencedUpdateAsync(
        DbConnection connection, DbTransaction transaction, string sql, Action<DbCommand> bind, DeliveryAttempt attempt, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            bind(command);
            BindFence(command, attempt);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private void BindFence(DbCommand command, DeliveryAttempt attempt)
    {
        RelationalSqlBatch.AddParameter(command, "@id", attempt.Delivery.Id.ToString());
        RelationalSqlBatch.AddParameter(command, "@claimed_by", attempt.Claimant);
        RelationalSqlBatch.AddParameter(command, "@attempts", attempt.Delivery.Attempts);
        RelationalSqlBatch.AddParameter(command, "@now", Now);
    }

    private async Task InsertAttemptAsync(
        DbConnection connection, DbTransaction transaction, DeliveryAttempt attempt, string outcome, string? errorType, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = DeliveryTable.AttemptSql(_attempts);
            RelationalSqlBatch.AddParameter(command, "@id", attempt.Delivery.Id.ToString());
            RelationalSqlBatch.AddParameter(command, "@started_at", StoredInstant.Text(attempt.StartedAt));
            RelationalSqlBatch.AddParameter(command, "@duration_ms", (int)Math.Min(int.MaxValue, attempt.Duration.TotalMilliseconds));
            RelationalSqlBatch.AddParameter(command, "@outcome", outcome);
            RelationalSqlBatch.AddParameter(command, "@error_type", (object?)errorType ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One transaction behind the database's write gate. <paramref name="work"/> opens it with a write, never a read
    /// (the port's remarks): SQLite then holds the write lock from the first statement instead of upgrading a read lock.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(Func<DbConnection, DbTransaction, Task<T>> work, CancellationToken ct)
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
                    var result = await work(connection, transaction).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    return result;
                }
            }
        }
    }

    private static async Task<int> ExecuteInAsync(DbConnection connection, DbTransaction transaction, string sql, Action<DbCommand> bind, CancellationToken ct)
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

    private async Task<int> ExecuteAsync(string sql, Action<DbCommand> bind, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = sql;
                    bind(command);

                    return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
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

    private string Now => StoredInstant.Text(_time.GetUtcNow());
}
