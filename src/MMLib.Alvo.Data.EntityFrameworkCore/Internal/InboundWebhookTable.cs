using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// <c>&lt;prefix&gt;_inbound_webhooks</c>: one row per Standard Webhooks id a receiver has seen, claimed with a fencing token,
/// swept after seven days.
/// </summary>
internal static class InboundWebhookTable
{
    private const string IdMatches = "receiver = @receiver AND webhook_id = @id";

    /// <summary>The fence: completing or releasing applies only while the row still carries the run's token.</summary>
    private const string Holds = "claim_token = @token";

    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.InboundWebhooksSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            receiver TEXT NOT NULL,
            webhook_id TEXT NOT NULL,
            claim_token TEXT NOT NULL,
            received_at TEXT NOT NULL,
            lease_until TEXT NOT NULL,
            completed_at TEXT NULL,
            PRIMARY KEY (receiver, webhook_id)
        )
        """;

    internal static string IndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS {table}_received ON {table} (received_at)";

    internal static async Task EnsureAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await ExecutedAsync(Command(connection, null, Ddl(table), null), ct).ConfigureAwait(false);
        await ExecutedAsync(Command(connection, null, IndexDdl(table), null), ct).ConfigureAwait(false);
    }

    internal static async Task<WebhookClaim?> FindAsync(
        DbConnection connection, DbTransaction transaction, string table, string receiver, string id, DateTimeOffset now,
        CancellationToken ct)
    {
        var command = Command(
            connection, transaction, $"SELECT lease_until, completed_at FROM {table} WHERE {IdMatches}", (receiver, id));
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? Found(reader, now) : null;
            }
        }
    }

    internal static Task<int> InsertAsync(
        DbConnection connection, DbTransaction transaction, string table, string receiver, string id, string token,
        DateTimeOffset now, CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                transaction,
                $"INSERT INTO {table} (receiver, webhook_id, claim_token, received_at, lease_until) "
                + "VALUES (@receiver, @id, @token, @received_at, @lease_until)",
                (receiver, id),
                Lease(token, now)),
            ct);

    internal static Task<int> ReclaimAsync(
        DbConnection connection, DbTransaction transaction, string table, string receiver, string id, string token,
        DateTimeOffset now, CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                transaction,
                $"UPDATE {table} SET claim_token = @token, received_at = @received_at, lease_until = @lease_until "
                + $"WHERE {IdMatches} AND completed_at IS NULL AND lease_until <= @received_at",
                (receiver, id),
                Lease(token, now)),
            ct);

    internal static Task<int> CompleteAsync(
        DbConnection connection, string table, string receiver, string id, string token, DateTimeOffset now,
        CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                null,
                $"UPDATE {table} SET completed_at = @completed_at WHERE {IdMatches} AND {Holds} AND completed_at IS NULL",
                (receiver, id),
                [("@token", token), ("@completed_at", StoredInstant.Text(now))]),
            ct);

    internal static Task<int> ReleaseAsync(
        DbConnection connection, string table, string receiver, string id, string token, CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                null,
                $"DELETE FROM {table} WHERE {IdMatches} AND {Holds} AND completed_at IS NULL",
                (receiver, id),
                [("@token", token)]),
            ct);

    internal static Task<int> SweepAsync(DbConnection connection, string table, DateTimeOffset before, CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                null,
                $"DELETE FROM {table} WHERE received_at < @before",
                null,
                [("@before", StoredInstant.Text(before))]),
            ct);

    private static WebhookClaim Found(DbDataReader reader, DateTimeOffset now) =>
        !reader.IsDBNull(1) ? WebhookClaim.Completed
        : string.CompareOrdinal(reader.GetString(0), StoredInstant.Text(now)) > 0 ? WebhookClaim.InFlight
        : WebhookClaim.Claimed;

    private static (string Name, object? Value)[] Lease(string token, DateTimeOffset now) =>
    [
        ("@token", token),
        ("@received_at", StoredInstant.Text(now)),
        ("@lease_until", StoredInstant.Text(now + InboundWebhookLimits.Lease)),
    ];

    private static DbCommand Command(
        DbConnection connection, DbTransaction? transaction, string sql, (string Receiver, string Id)? webhook,
        params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (webhook is var (receiver, id))
        {
            RelationalSqlBatch.AddParameter(command, "@receiver", receiver);
            RelationalSqlBatch.AddParameter(command, "@id", id);
        }

        foreach (var (name, value) in values)
        {
            RelationalSqlBatch.AddParameter(command, name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task<int> ExecutedAsync(DbCommand command, CancellationToken ct)
    {
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
