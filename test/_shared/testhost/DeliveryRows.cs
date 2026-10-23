using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>One row of <c>alvo_deliveries</c>, as a fact reads it.</summary>
internal sealed record DeliveryRow(Guid EventId, string Subscriber, string Status, int Attempts, string? LastErrorType);

/// <summary>Reads the delivery tables straight off the database — the port has no read member until HX-06.</summary>
internal static class DeliveryRows
{
    internal static async Task<IReadOnlyList<DeliveryRow>> ReadAsync(Func<DbConnection> connect, CancellationToken ct)
    {
        await using var connection = connect();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, subscriber, status, attempts, last_error_type FROM alvo_deliveries ORDER BY event_id, subscriber";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<DeliveryRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DeliveryRow(
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    internal static async Task<int> AttemptCountAsync(Func<DbConnection> connect, CancellationToken ct)
    {
        await using var connection = connect();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM alvo_delivery_attempts";

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
}
