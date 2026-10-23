using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The delivery tables' DDL and every statement over them — identical on both shipped engines, as the outbox's are.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim's outer condition repeats the subquery's</b> for the outbox's measured reason (spike Q4): under
/// PostgreSQL's <c>READ COMMITTED</c>, a claimant blocked behind another re-checks the outer <c>WHERE</c> once the lock
/// clears, and nothing else. Without the repetition a lane head is claimed twice.
/// </para>
/// <para>
/// <b><c>ON CONFLICT … DO NOTHING</c>, <c>LIMIT</c> and <c>RETURNING</c> are SQLite's and PostgreSQL's spelling, not
/// ANSI.</b> The outbox already spells its claim the same way; the T-SQL dialect question for Alvo's own tables belongs
/// to the Azure SQL provider (#92).
/// </para>
/// </remarks>
internal static class DeliveryTable
{
    internal const string Pending = "pending";
    internal const string Running = "running";
    internal const string Succeeded = "succeeded";
    internal const string Retrying = "retrying";
    internal const string Dead = "dead";
    internal const string Cancelled = "cancelled";
    internal const string FailedOutcome = "failed";

    private const string Claimable =
        "((status IN ('pending', 'retrying') AND not_before <= @now) OR (status = 'running' AND lease_until < @now))";

    private const string Unfinished = "status IN ('pending', 'running', 'retrying')";

    private const string Fence = "id = @id AND claimed_by = @claimed_by AND attempts = @attempts AND status = 'running'";

    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.DeliveriesSuffix;

    internal static string AttemptsNameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.DeliveryAttemptsSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            id TEXT NOT NULL PRIMARY KEY,
            event_id TEXT NOT NULL,
            subscriber TEXT NOT NULL,
            partition_key TEXT NOT NULL,
            tenant_id TEXT NULL,
            kind TEXT NOT NULL,
            status TEXT NOT NULL,
            attempts INTEGER NOT NULL,
            not_before TEXT NOT NULL,
            lease_until TEXT NULL,
            claimed_by TEXT NULL,
            completed_at TEXT NULL,
            last_error_type TEXT NULL,
            UNIQUE (event_id, subscriber)
        )
        """;

    internal static string ClaimIndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS ix_{table}_claim ON {table} (status, not_before)";

    internal static string LaneIndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS ix_{table}_lane ON {table} (subscriber, partition_key, event_id)";

    internal static string AttemptsDdl(string attempts) =>
        $"""
        CREATE TABLE IF NOT EXISTS {attempts} (
            delivery_id TEXT NOT NULL,
            attempt INTEGER NOT NULL,
            started_at TEXT NOT NULL,
            duration_ms INTEGER NOT NULL,
            outcome TEXT NOT NULL,
            error_type TEXT NULL,
            PRIMARY KEY (delivery_id, attempt)
        )
        """;

    /// <summary>
    /// A <b>partial</b> index on the outbox's undispatched entries, (partition key, id), so the lane condition's look at
    /// earlier undispatched entries of a key is a seek over the few that are still waiting, not a scan of the key's whole
    /// history — the outbox is never purged. Both engines use it because the query carries the same
    /// <c>dispatched_at IS NULL</c> term verbatim (SQLite requires that). An index, not an <c>ALTER</c> of
    /// <c>alvo_outbox</c> (spec E10).
    /// </summary>
    internal static string OutboxUnfannedIndexDdl(string outbox) =>
        $"CREATE INDEX IF NOT EXISTS ix_{outbox}_unfanned ON {outbox} (partition_key, id) WHERE dispatched_at IS NULL";

    internal static IReadOnlyList<string> Statements(string table, string attempts, string outbox) =>
        [Ddl(table), ClaimIndexDdl(table), LaneIndexDdl(table), AttemptsDdl(attempts), OutboxUnfannedIndexDdl(outbox)];

    internal static async Task EnsureAsync(DbConnection connection, string table, string attempts, string outbox, CancellationToken ct)
    {
        foreach (var statement in Statements(table, attempts, outbox))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    internal static string FanOutSql(string table, int rows) =>
        $"""
        INSERT INTO {table} (id, event_id, subscriber, partition_key, tenant_id, kind, status, attempts, not_before, completed_at, last_error_type)
        VALUES {string.Join(", ", Enumerable.Range(0, rows).Select(FanOutRow))}
        ON CONFLICT (event_id, subscriber) DO NOTHING
        """;

    private static string FanOutRow(int row) =>
        $"(@id{row}, @event_id{row}, @subscriber{row}, @partition_key{row}, @tenant_id{row}, @kind{row}, @status{row}, 0, @now, @completed_at{row}, @last_error_type{row})";

    internal static string ClaimSql(string table, string outbox, int ordered, int unordered)
    {
        var condition = $"{Claimable} AND {Registered(ordered + unordered)} AND {LaneHead(table, outbox, ordered, unordered)}";

        return
            $"""
            UPDATE {table} SET status = 'running', claimed_by = @claimed_by, attempts = attempts + 1, lease_until = @lease_until
             WHERE {condition}
               AND id IN (SELECT id FROM {table}
                           WHERE {condition}
                           ORDER BY event_id, subscriber
                           LIMIT @batch)
            RETURNING id, event_id, subscriber, partition_key, attempts
            """;
    }

    private static string Registered(int subscribers) => $"subscriber IN ({Parameters(0, subscribers)})";

    private static string LaneHead(string table, string outbox, int ordered, int unordered)
    {
        var noEarlier =
            $"NOT EXISTS (SELECT 1 FROM {table} earlier WHERE earlier.subscriber = {table}.subscriber "
            + $"AND earlier.partition_key = {table}.partition_key AND earlier.event_id < {table}.event_id "
            + "AND (earlier.status IN ('pending', 'running', 'retrying') "
            + "OR (earlier.status = 'dead' AND earlier.last_error_type <> 'unbound')))";
        var noUnfanned =
            $"NOT EXISTS (SELECT 1 FROM {outbox} unfanned WHERE unfanned.partition_key = {table}.partition_key "
            + $"AND unfanned.id < {table}.event_id AND unfanned.dispatched_at IS NULL "
            + "AND (unfanned.attempts < @outbox_max_attempts "
            + "OR (unfanned.claimed_by IS NOT NULL AND unfanned.claimed_at >= @outbox_stale_before)))";
        var laneHead = $"{noEarlier} AND {noUnfanned}";

        return unordered == 0 ? laneHead : $"(subscriber IN ({Parameters(ordered, unordered)}) OR ({laneHead}))";
    }

    private static string Parameters(int from, int count) =>
        string.Join(", ", Enumerable.Range(from, count).Select(index => $"@s{index}"));

    internal static string PayloadsSql(string outbox, int events) =>
        $"SELECT id, payload FROM {outbox} WHERE id IN ({string.Join(", ", Enumerable.Range(0, events).Select(index => $"@e{index}"))})";

    internal static string CompleteSql(string table) =>
        $"UPDATE {table} SET status = 'succeeded', completed_at = @now, lease_until = NULL, last_error_type = NULL WHERE {Fence}";

    internal static string FailSql(string table) =>
        $"""
        UPDATE {table} SET status = CASE WHEN @retryable = 0 OR attempts >= @max_attempts THEN 'dead' ELSE 'retrying' END,
                           completed_at = CASE WHEN @retryable = 0 OR attempts >= @max_attempts THEN @now ELSE NULL END,
                           not_before = @not_before, last_error_type = @error_type, lease_until = NULL
         WHERE {Fence}
        """;

    internal static string AttemptSql(string attempts) =>
        $"""
        INSERT INTO {attempts} (delivery_id, attempt, started_at, duration_ms, outcome, error_type)
        VALUES (@id, @attempts, @started_at, @duration_ms, @outcome, @error_type)
        """;

    internal static string PendingSubscribersSql(string table) =>
        $"SELECT DISTINCT subscriber FROM {table} WHERE {Unfinished} ORDER BY subscriber";

    internal static string EndPendingSql(string table) =>
        $"UPDATE {table} SET status = @status, last_error_type = @reason, completed_at = @now, lease_until = NULL WHERE subscriber = @subscriber AND {Unfinished}";
}
