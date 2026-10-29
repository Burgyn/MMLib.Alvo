using MMLib.Alvo.Events;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>The operator's members of the relational <see cref="IDeliveryStore"/>, over <see cref="DeliveryOperationsTable"/>'s SQL.</summary>
internal sealed partial class EfCoreDeliveryStore
{
    public async Task<IReadOnlyList<StoredDelivery>> ListAsync(DeliveryFilter filter, CancellationToken cancellationToken = default)
    {
        EnsureRows(filter);

        return await ReadAsync(
            DeliveryOperationsTable.ListSql(_table, filter),
            command => BindFilter(command, filter, keyset: true, limit: true),
            ReadDelivery,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredDelivery?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync(DeliveryOperationsTable.FindSql(_table), command => BindId(command, id), ReadDelivery, cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var payloads = await PayloadsAsync([rows[0].EventId], cancellationToken).ConfigureAwait(false);

        return rows[0] with { Payload = payloads.GetValueOrDefault(rows[0].EventId) };
    }

    public async Task<IReadOnlyList<StoredDeliveryAttempt>> AttemptsAsync(Guid id, CancellationToken cancellationToken = default) =>
        await ReadAsync(DeliveryOperationsTable.AttemptsSql(_attempts), command => BindId(command, id), ReadAttempt, cancellationToken)
            .ConfigureAwait(false);

    public async Task<DeliverySelection> SelectAsync(DeliveryFilter filter, CancellationToken cancellationToken = default)
    {
        EnsureRows(filter);
        var rows = await ReadAsync(
            DeliveryOperationsTable.SelectSql(_table, filter),
            command => BindFilter(command, filter, keyset: false, limit: true),
            reader => (
                Id: Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                Total: Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Busy: Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0
            ? new DeliverySelection { Count = 0, Sample = [] }
            : new DeliverySelection { Count = rows[0].Total, Busy = rows[0].Busy, Sample = [.. rows.Select(row => row.Id)] };
    }

    public async Task<bool> RedeliverAsync(Guid id, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        EnsureEntry(entry);
        var subscribers = await ReadAsync(DeliveryOperationsTable.SubscriberOfSql(_table), command => BindId(command, id), reader => reader.GetString(0), cancellationToken)
            .ConfigureAwait(false);

        return subscribers.Count == 1 && await InTransactionAsync(async (connection, transaction) =>
        {
            await LockSubscribersAsync(connection, transaction, subscribers, exclusive: true, cancellationToken).ConfigureAwait(false);
            var reset = await ExecuteInAsync(connection, transaction, DeliveryOperationsTable.RedeliverSql(_table), command =>
            {
                BindId(command, id);
                RelationalSqlBatch.AddParameter(command, "@now", Now);
            }, cancellationToken).ConfigureAwait(false) == 1;

            if (reset)
            {
                await LogChangeAsync(connection, transaction, entry with { Affected = 1 }, cancellationToken).ConfigureAwait(false);
            }

            return reset;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The log row first — the transaction's opening write, and the key's claim: a concurrent replay under the same key waits
    /// on the key's unique index, then inserts nothing and answers from the first's row — then the reset.
    /// </summary>
    public Task<DeliveryOperationEntry?> ReplayAsync(DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        EnsureRows(filter);
        EnsureEntry(entry);

        return InTransactionAsync(async (connection, transaction) =>
            await LogAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false) == 1
                ? await ResetSelectedAsync(connection, transaction, filter, entry, cancellationToken).ConfigureAwait(false)
                : await SpentKeyAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }

    public async Task<DeliveryOperationEntry?> FindOperationAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var rows = await ReadAsync(DeliveryOperationsTable.FindLogSql(_log), command => BindKey(command, scope, key), ReadEntry, cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Answers a replay whose log row was skipped: <see langword="null"/> when its key is recorded — the replay already
    /// happened — and otherwise throws, since the row was refused for its id and the replay must not report what it did not log.
    /// </summary>
    private async Task<DeliveryOperationEntry?> SpentKeyAsync(DbConnection connection, DbTransaction transaction, DeliveryOperationEntry entry, CancellationToken ct)
    {
        if (entry.IdempotencyKey is { } key && entry.IdempotencyScope is { } scope)
        {
            var recorded = await RelationalSqlBatch.ReadRowsAsync(
                connection, transaction, DeliveryOperationsTable.FindLogSql(_log), command => BindKey(command, scope, key), ReadEntry, ct).ConfigureAwait(false);
            if (recorded.Count == 1)
            {
                return null;
            }
        }

        throw new InvalidOperationException($"The operations log already holds row {entry.Id}; the {entry.Operation} reset nothing.");
    }

    /// <summary>
    /// Locks the subscribers the filter may touch exclusively, resets what it selects, and writes the outcome onto the log
    /// row — the count, and the first <see cref="DeliveryFilter.Limit"/> ids in event order.
    /// </summary>
    private async Task<DeliveryOperationEntry> ResetSelectedAsync(
        DbConnection connection, DbTransaction transaction, DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken ct)
    {
        var subscribers = await ReplaySubscribersAsync(connection, transaction, filter, ct).ConfigureAwait(false);
        var reset = subscribers.Count == 0 ? [] : await ResetLockedAsync(connection, transaction, filter, subscribers, ct).ConfigureAwait(false);
        var recorded = entry with
        {
            Affected = reset.Count,
            Sample = [.. reset.OrderBy(row => row.EventId, StringComparer.Ordinal).ThenBy(row => row.Id, StringComparer.Ordinal)
                .Take(filter.Limit).Select(row => Guid.Parse(row.Id, CultureInfo.InvariantCulture))],
        };
        await ExecuteInAsync(connection, transaction, DeliveryOperationsTable.LogOutcomeSql(_log), command => BindOutcome(command, recorded), ct)
            .ConfigureAwait(false);

        return recorded;
    }

    /// <summary>The filter's subscriber, or — naming none — every subscriber with a replayable delivery it selects, read in the transaction.</summary>
    private async Task<IReadOnlyList<string>> ReplaySubscribersAsync(DbConnection connection, DbTransaction transaction, DeliveryFilter filter, CancellationToken ct) =>
        filter.Subscriber is { } subscriber
            ? [subscriber]
            : await RelationalSqlBatch.ReadRowsAsync(
                connection,
                transaction,
                DeliveryOperationsTable.ReplaySubscribersSql(_table, filter),
                command => BindFilter(command, filter, keyset: false, limit: false),
                reader => reader.GetString(0),
                ct).ConfigureAwait(false);

    private async Task<List<(string Id, string EventId)>> ResetLockedAsync(
        DbConnection connection, DbTransaction transaction, DeliveryFilter filter, IReadOnlyList<string> subscribers, CancellationToken ct)
    {
        await LockSubscribersAsync(connection, transaction, subscribers, exclusive: true, ct).ConfigureAwait(false);

        return await RelationalSqlBatch.ReadRowsAsync(connection, transaction, DeliveryOperationsTable.ReplaySql(_table, filter, subscribers.Count), command =>
        {
            BindFilter(command, filter, keyset: false, limit: false);
            RelationalSqlBatch.AddParameter(command, "@now", Now);
            if (filter.Subscriber is null)
            {
                BindLockedSubscribers(command, subscribers);
            }
        }, reader => (Id: reader.GetString(0), EventId: reader.GetString(1)), ct).ConfigureAwait(false);
    }

    private static void BindLockedSubscribers(DbCommand command, IReadOnlyList<string> subscribers)
    {
        for (var index = 0; index < subscribers.Count; index++)
        {
            RelationalSqlBatch.AddParameter(command, $"@replay_subscriber{index}", subscribers[index]);
        }
    }

    /// <summary>
    /// Takes the subscriber's lock exclusively — so the pause waits for a claim statement already under way and every later
    /// claim statement sees it — then writes the control, and logs the change in the same transaction when there was one.
    /// </summary>
    public Task<bool> SetPausedAsync(string subscriber, bool paused, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);
        EnsureEntry(entry);

        return InTransactionAsync(async (connection, transaction) =>
        {
            await LockSubscribersAsync(connection, transaction, [subscriber], exclusive: true, cancellationToken).ConfigureAwait(false);
            var sql = paused ? DeliveryOperationsTable.PauseSql(_controls) : DeliveryOperationsTable.ResumeSql(_controls);
            var changed = await ExecuteInAsync(connection, transaction, sql, command => BindControl(command, subscriber, paused, entry), cancellationToken)
                .ConfigureAwait(false) == 1;
            if (changed)
            {
                await LogChangeAsync(connection, transaction, entry with { Affected = 1 }, cancellationToken).ConfigureAwait(false);
            }

            return changed;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SubscriberControl>> ControlsAsync(CancellationToken cancellationToken = default)
    {
        var controls = await ReadAsync(DeliveryOperationsTable.ControlsSql(_controls), _ => { }, ReadControl, cancellationToken).ConfigureAwait(false);

        return [.. controls.OrderBy(control => control.Subscriber, StringComparer.Ordinal)];
    }

    private static void BindControl(DbCommand command, string subscriber, bool paused, DeliveryOperationEntry entry)
    {
        RelationalSqlBatch.AddParameter(command, "@subscriber", subscriber);
        if (paused)
        {
            RelationalSqlBatch.AddParameter(command, "@at", StoredInstant.Text(entry.At));
            RelationalSqlBatch.AddParameter(command, "@actor", entry.Actor);
            RelationalSqlBatch.AddParameter(command, "@reason", Nullable(entry.Reason));
        }
    }

    private static SubscriberControl ReadControl(DbDataReader reader) => new()
    {
        Subscriber = reader.GetString(0),
        PausedAt = StoredInstant.Of(reader.GetValue(1)),
        PausedBy = reader.GetString(2),
        Reason = reader.IsDBNull(3) ? null : reader.GetString(3),
    };

    /// <summary>
    /// Refuses a log row whose idempotency key comes without the scope it is recorded under — before any transaction, so
    /// the log's <c>CHECK</c> never has to refuse it as an engine error after a change was made.
    /// </summary>
    private static void EnsureEntry(DeliveryOperationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IdempotencyKey is not null && entry.IdempotencyScope is null)
        {
            throw new ArgumentException("An idempotency key is recorded under its caller's scope; the entry carries none.", nameof(entry));
        }
    }

    private static void BindKey(DbCommand command, string scope, string key)
    {
        RelationalSqlBatch.AddParameter(command, "@scope", scope);
        RelationalSqlBatch.AddParameter(command, "@key", key);
    }

    /// <summary>
    /// Logs a change already made in <paramref name="transaction"/>, or throws so the transaction rolls the change back: a
    /// row skipped by <c>ON CONFLICT DO NOTHING</c> means its id or key is spent, and a change must never stand unlogged.
    /// </summary>
    private async Task LogChangeAsync(DbConnection connection, DbTransaction transaction, DeliveryOperationEntry entry, CancellationToken ct)
    {
        if (await LogAsync(connection, transaction, entry, ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"The operations log already holds row {entry.Id} or its idempotency key; the {entry.Operation} was rolled back.");
        }
    }

    internal static DeliveryOperationEntry ReadEntry(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        At = StoredInstant.Of(reader.GetValue(1)),
        Actor = reader.GetString(2),
        Operation = reader.GetString(3),
        Target = reader.GetString(4),
        Reason = reader.IsDBNull(5) ? null : reader.GetString(5),
        Affected = reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
        Sample = reader.IsDBNull(7) ? [] : [.. reader.GetString(7).Split(',').Select(id => Guid.Parse(id, CultureInfo.InvariantCulture))],
        IdempotencyScope = reader.IsDBNull(8) ? null : reader.GetString(8),
        IdempotencyKey = reader.IsDBNull(9) ? null : reader.GetString(9),
        Fingerprint = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private Task<int> LogAsync(DbConnection connection, DbTransaction transaction, DeliveryOperationEntry entry, CancellationToken ct) =>
        ExecuteInAsync(connection, transaction, DeliveryOperationsTable.InsertLogSql(_log), command => BindEntry(command, entry), ct);

    private static void BindEntry(DbCommand command, DeliveryOperationEntry entry)
    {
        RelationalSqlBatch.AddParameter(command, "@log_at", StoredInstant.Text(entry.At));
        RelationalSqlBatch.AddParameter(command, "@log_actor", entry.Actor);
        RelationalSqlBatch.AddParameter(command, "@log_operation", entry.Operation);
        RelationalSqlBatch.AddParameter(command, "@log_target", entry.Target);
        RelationalSqlBatch.AddParameter(command, "@log_reason", Nullable(entry.Reason));
        RelationalSqlBatch.AddParameter(command, "@log_scope", Nullable(entry.IdempotencyScope));
        RelationalSqlBatch.AddParameter(command, "@log_key", Nullable(entry.IdempotencyKey));
        RelationalSqlBatch.AddParameter(command, "@log_fingerprint", Nullable(entry.Fingerprint));
        BindOutcome(command, entry);
    }

    private static void BindOutcome(DbCommand command, DeliveryOperationEntry entry)
    {
        RelationalSqlBatch.AddParameter(command, "@log_id", entry.Id.ToString());
        RelationalSqlBatch.AddParameter(command, "@log_affected", (object?)entry.Affected ?? DBNull.Value);
        RelationalSqlBatch.AddParameter(command, "@log_sample", entry.Sample.Count == 0 ? DBNull.Value : string.Join(',', entry.Sample));
    }

    private static object Nullable(string? value) => (object?)value ?? DBNull.Value;

    private static void EnsureRows(DeliveryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.Limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(filter.Limit, DeliveryOperationsTable.MaxPage);
    }

    private static void BindId(DbCommand command, Guid id) => RelationalSqlBatch.AddParameter(command, "@id", id.ToString());

    private static void BindFilter(DbCommand command, DeliveryFilter filter, bool keyset, bool limit)
    {
        AddWhenSet(command, "@subscriber", filter.Subscriber);
        AddWhenSet(command, "@partition_key", filter.PartitionKey);
        AddWhenSet(command, "@status", filter.Status);
        AddWhenSet(command, "@from_event", filter.FromEventId?.ToString());
        AddWhenSet(command, "@to_event", filter.ToEventId?.ToString());
        AddWhenSet(command, "@after", keyset ? filter.After?.ToString() : null);
        if (limit)
        {
            RelationalSqlBatch.AddParameter(command, "@limit", filter.Limit);
        }
    }

    private static void AddWhenSet(DbCommand command, string name, string? value)
    {
        if (value is not null)
        {
            RelationalSqlBatch.AddParameter(command, name, value);
        }
    }

    private static StoredDelivery ReadDelivery(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        EventId = Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        Subscriber = reader.GetString(2),
        PartitionKey = reader.GetString(3),
        TenantId = reader.IsDBNull(4) ? null : reader.GetString(4),
        Kind = reader.GetString(5),
        Status = reader.GetString(6),
        Attempts = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
        NotBefore = StoredInstant.Of(reader.GetValue(8)),
        CompletedAt = reader.IsDBNull(9) ? null : StoredInstant.Of(reader.GetValue(9)),
        LastErrorType = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private static StoredDeliveryAttempt ReadAttempt(DbDataReader reader) => new()
    {
        Attempt = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
        StartedAt = StoredInstant.Of(reader.GetValue(1)),
        Duration = TimeSpan.FromMilliseconds(Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)),
        Outcome = reader.GetString(3),
        ErrorType = reader.IsDBNull(4) ? null : reader.GetString(4),
    };
}
