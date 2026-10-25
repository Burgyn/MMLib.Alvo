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
        ArgumentNullException.ThrowIfNull(entry);
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

            return reset && await LogAsync(connection, transaction, entry with { Affected = 1 }, cancellationToken).ConfigureAwait(false) == 1;
        }, cancellationToken).ConfigureAwait(false);
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
