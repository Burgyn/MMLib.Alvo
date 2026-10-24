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
