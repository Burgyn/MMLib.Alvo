using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// <c>&lt;prefix&gt;_command_requests</c>: one row per command <c>Idempotency-Key</c> — claimed (with a fencing token),
/// committed, answered. ANSI SQL only, identical on every engine; instants are <see cref="StoredInstant"/> text, which
/// orders lexically. Every write after the claim names the token (Ruling 36).
/// </summary>
internal static class CommandLedgerTable
{
    private const string KeyMatches = "scope = @scope AND idempotency_key = @key";

    /// <summary>The fence: a write after the claim applies only while the row still carries the run's token.</summary>
    private const string Holds = "claim_token = @token";

    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.CommandRequestsSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            scope TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            claim_token TEXT NOT NULL,
            claimed_at TEXT NOT NULL,
            lease_until TEXT NOT NULL,
            committed_at TEXT NULL,
            answer_status INTEGER NULL,
            answer_body TEXT NULL,
            answer_media_type TEXT NULL,
            PRIMARY KEY (scope, idempotency_key)
        )
        """;

    internal static string IndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS {table}_claimed ON {table} (claimed_at)";

    internal static async Task EnsureAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await ExecutedAsync(Command(connection, null, Ddl(table), null), ct).ConfigureAwait(false);
        await ExecutedAsync(Command(connection, null, IndexDdl(table), null), ct).ConfigureAwait(false);
    }

    /// <summary><paramref name="key"/>, refused when it carries no claim's token — a caller's bug, not a lost lease.</summary>
    /// <exception cref="InvalidOperationException">The key was never claimed.</exception>
    internal static CommandKey Claimed(CommandKey key) =>
        string.IsNullOrEmpty(key.Token)
            ? throw new InvalidOperationException(
                $"The command key '{key.Key}' was never claimed: only a key ClaimAsync granted a token to can be committed.")
            : key;

    internal static async Task<CommandClaim?> FindAsync(
        DbConnection connection, DbTransaction? transaction, string table, CommandKey key, DateTimeOffset now,
        CancellationToken ct)
    {
        var command = Command(
            connection,
            transaction,
            $"SELECT fingerprint, lease_until, committed_at, answer_status, answer_body, answer_media_type FROM {table} WHERE {KeyMatches}",
            key);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? Found(reader, key, now) : null;
            }
        }
    }

    internal static Task<int> InsertClaimAsync(
        DbConnection connection, DbTransaction transaction, string table, CommandKey key, string token, DateTimeOffset now,
        CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                transaction,
                $"INSERT INTO {table} (scope, idempotency_key, fingerprint, claim_token, claimed_at, lease_until) "
                + "VALUES (@scope, @key, @fingerprint, @token, @claimed_at, @lease_until)",
                key,
                [("@fingerprint", key.Fingerprint), .. Lease(token, now)]),
            ct);

    internal static Task<int> ReclaimAsync(
        DbConnection connection, DbTransaction transaction, string table, CommandKey key, string token, DateTimeOffset now,
        CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                transaction,
                $"UPDATE {table} SET claim_token = @token, claimed_at = @claimed_at, lease_until = @lease_until "
                + $"WHERE {KeyMatches} AND committed_at IS NULL AND lease_until <= @claimed_at",
                key,
                Lease(token, now)),
            ct);

    internal static Task<int> CommitAsync(
        DbConnection connection, DbTransaction transaction, string table, CommandKey key, DateTimeOffset now,
        CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                transaction,
                $"UPDATE {table} SET committed_at = @committed_at WHERE {KeyMatches} AND {Holds} AND committed_at IS NULL",
                key,
                [("@token", key.Token), ("@committed_at", StoredInstant.Text(now))]),
            ct);

    internal static Task<int> AnswerAsync(
        DbConnection connection, DbTransaction? transaction, string table, CommandKey key, CommandAnswer answer,
        CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                transaction,
                $"UPDATE {table} SET answer_status = @status, answer_body = @body, answer_media_type = @media_type "
                + $"WHERE {KeyMatches} AND {Holds}",
                key,
                [("@token", key.Token), ("@status", answer.Status), ("@body", answer.Body), ("@media_type", answer.MediaType)]),
            ct);

    internal static Task<int> ReleaseAsync(DbConnection connection, string table, CommandKey key, CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                null,
                $"DELETE FROM {table} WHERE {KeyMatches} AND {Holds} AND committed_at IS NULL",
                key,
                [("@token", key.Token)]),
            ct);

    internal static Task<int> SweepAsync(
        DbConnection connection, string table, DateTimeOffset claimedBefore, CancellationToken ct) =>
        ExecutedAsync(
            Command(
                connection,
                null,
                $"DELETE FROM {table} WHERE claimed_at < @before",
                null,
                [("@before", StoredInstant.Text(claimedBefore))]),
            ct);

    private static (string Name, object? Value)[] Lease(string token, DateTimeOffset now) =>
    [
        ("@token", token),
        ("@claimed_at", StoredInstant.Text(now)),
        ("@lease_until", StoredInstant.Text(now + CommandLedgerLimits.Lease)),
    ];

    private static CommandClaim Found(DbDataReader reader, CommandKey key, DateTimeOffset now)
    {
        if (!string.Equals(reader.GetString(0), key.Fingerprint, StringComparison.Ordinal))
        {
            return CommandClaim.Mismatch;
        }

        if (!reader.IsDBNull(3))
        {
            return CommandClaim.Answered(AnswerOf(reader));
        }

        return !reader.IsDBNull(2) ? CommandClaim.Unanswered
            : string.CompareOrdinal(reader.GetString(1), StoredInstant.Text(now)) > 0 ? CommandClaim.InFlight
            : CommandClaim.Claimable;
    }

    private static CommandAnswer AnswerOf(DbDataReader reader) => new(
        Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5));

    private static DbCommand Command(
        DbConnection connection, DbTransaction? transaction, string sql, CommandKey? key,
        params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (key is not null)
        {
            RelationalSqlBatch.AddParameter(command, "@scope", key.Scope);
            RelationalSqlBatch.AddParameter(command, "@key", key.Key);
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
