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
/// <para>
/// <b>An attempt is numbered after the delivery's last recorded one.</b> The row's <c>attempts</c> counts claims since the
/// delivery was last put back to pending — what the retry policy and the attempt ceiling read — while the history numbers
/// every attempt the delivery ever had, so a redelivered row neither collides with its own history nor loses it.
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
    internal const string Superseded = "superseded";
    internal const string FailedOutcome = "failed";
    internal const string SkippedOutcome = "skipped";

    private const string Claimable =
        "((status IN ('pending', 'retrying') AND not_before <= @now) OR (status = 'running' AND lease_until < @now))";

    private const string Unfinished = "status IN ('pending', 'running', 'retrying')";

    private const string LaneBlocking = "status IN ('pending', 'running', 'retrying', 'dead')";

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

    /// <summary>
    /// A <b>partial</b> index on the deliveries that can still hold a lane back — (subscriber, partition key, event id)
    /// over the pending, running, retrying and dead rows — so the claim's look at a lane head's earlier deliveries is a
    /// seek over the few that are unfinished, not a walk through the lane's whole succeeded history, which is never
    /// purged. The claim carries the index's <c>status IN (…)</c> term verbatim, which is what lets SQLite (and
    /// PostgreSQL's predicate proof) use it. An addition beside the spec's <c>ix_&lt;table&gt;_lane</c>, not a change of it.
    /// </summary>
    internal static string OpenLaneIndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS ix_{table}_lane_open ON {table} (subscriber, partition_key, event_id) WHERE {LaneBlocking}";

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

    /// <summary>
    /// The deliveries table, its attempts and steps tables and their indexes, each through <see cref="IfNotExistsDdl"/> so a replica that loses the
    /// race to create one of them to another replica takes the rival's object as its own.
    /// </summary>
    internal static async Task EnsureAsync(DbConnection connection, string table, string attempts, string outbox, string steps, CancellationToken ct)
    {
        await IfNotExistsDdl.CreateTableAsync(connection, table, Ddl(table), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, ClaimIndexDdl(table), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, LaneIndexDdl(table), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, OpenLaneIndexDdl(table), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, attempts, AttemptsDdl(attempts), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, OutboxUnfannedIndexDdl(outbox), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, steps, StepTable.Ddl(steps), ct).ConfigureAwait(false);
    }

    internal static string FanOutSql(string table, int rows) =>
        $"""
        INSERT INTO {table} (id, event_id, subscriber, partition_key, tenant_id, kind, status, attempts, not_before, completed_at, last_error_type)
        VALUES {string.Join(", ", Enumerable.Range(0, rows).Select(FanOutRow))}
        ON CONFLICT (event_id, subscriber) DO NOTHING
        """;

    private static string FanOutRow(int row) =>
        $"(@id{row}, @event_id{row}, @subscriber{row}, @partition_key{row}, @tenant_id{row}, @kind{row}, @status{row}, 0, @not_before{row}, @completed_at{row}, @last_error_type{row})";

    /// <summary>
    /// Supersedes every waiting delivery of one lane — subscriber, partition key and tenant — whose event is older than the
    /// lane's newest live one; run after the insert, in its transaction, once per debounced lane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The status term sits in the outer <c>WHERE</c></b>, so on PostgreSQL a claim that turned the row running first wins:
    /// the blocked supersede re-checks the row and skips it; supersede first, and the claim's own outer re-check fails.
    /// </para>
    /// <para>
    /// <b>Only a delivery that never started is superseded</b> (no attempt recorded): a sleeping one has done part of its
    /// work, and a retried one has tried it. The lane's newest counts only live rows — pending, retrying, running or
    /// succeeded — so a re-fan-out of an event whose own row ended dead, cancelled or superseded makes nothing newer.
    /// </para>
    /// <para>
    /// The tenant term keeps a custom event's shared partition key apart per tenant. The store takes the lane's lock
    /// exclusively first (<see cref="DebounceLockKey"/>), so two concurrent fan-outs of one lane each see the other's row.
    /// </para>
    /// </remarks>
    internal static string SupersedeSql(string table, string attempts) =>
        $"""
        UPDATE {table} SET status = 'superseded', completed_at = @now, lease_until = NULL
         WHERE subscriber = @subscriber AND partition_key = @partition_key
           AND COALESCE(tenant_id, '') = COALESCE(@tenant_id, '')
           AND status IN ('pending', 'retrying')
           AND NOT EXISTS (SELECT 1 FROM {attempts} started WHERE started.delivery_id = {table}.id)
           AND event_id < (SELECT MAX(newest.event_id) FROM {table} newest
                            WHERE newest.subscriber = @subscriber AND newest.partition_key = @partition_key
                              AND COALESCE(newest.tenant_id, '') = COALESCE(@tenant_id, '')
                              AND newest.status IN ('pending', 'retrying', 'running', 'succeeded'))
        """;

    /// <summary>The key of one debounced lane's transaction lock: the table, the subscriber, the tenant and the partition key.</summary>
    internal static long DebounceLockKey(string table, string subscriber, string? tenantId, string partitionKey) =>
        DeliveryOperationsTable.LockKey($"{table}:debounce:{subscriber}\u001f{tenantId}\u001f{partitionKey}");

    /// <summary>
    /// The claim: one conditional update whose outer condition repeats the claimability test — pending or due, a subscriber
    /// the claim names, not paused, the head of its lane — so a claimant blocked behind another claims nothing twice.
    /// </summary>
    /// <remarks>
    /// The pause is a <c>NOT EXISTS</c> over <paramref name="controls"/> in both halves, not a filter of the subscribers the
    /// dispatcher names: it holds on every replica and across restarts with no read-then-claim window. The store takes the
    /// subscriber's lock shared before this statement and a pause takes it exclusively, so on PostgreSQL a claim statement
    /// never runs on a snapshot that predates a committed pause.
    /// </remarks>
    internal static string ClaimSql(string table, string outbox, string controls, int ordered, int unordered)
    {
        var condition =
            $"{Claimable} AND {Registered(ordered + unordered)} AND {NotPaused(table, controls)} AND {LaneHead(table, outbox, ordered, unordered)}";

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

    /// <summary>
    /// The candidates of one capped subscriber — claimable as <see cref="ClaimSql"/> would claim them — at most
    /// <c>@limit</c> per slot key, oldest event first, up to <c>@candidates</c>. A hint the claim then tries slot by slot,
    /// never the enforcement: the slot rows and <see cref="ClaimOneSql"/> are.
    /// </summary>
    /// <remarks>
    /// The per-key cut keeps one saturated key — a tenant with many deliveries waiting — from filling the whole candidate
    /// read and hiding every other key's deliveries from the claim.
    /// </remarks>
    /// <param name="table">The validated deliveries table name.</param>
    /// <param name="outbox">The validated outbox table name.</param>
    /// <param name="controls">The validated subscriber controls table name.</param>
    /// <param name="ordered">Whether the subscriber's lanes are ordered.</param>
    /// <param name="perRecord">Whether the cap counts per record (partition key) of a tenant, not per tenant.</param>
    internal static string CandidatesSql(string table, string outbox, string controls, bool ordered, bool perRecord) =>
        $"""
        SELECT id, event_id, subscriber, partition_key, tenant_id FROM (
            SELECT id, event_id, subscriber, partition_key, tenant_id,
                   ROW_NUMBER() OVER (PARTITION BY subscriber, COALESCE(tenant_id, ''){(perRecord ? ", partition_key" : string.Empty)} ORDER BY event_id) AS place
              FROM {table}
             WHERE {OneSubscriber(table, outbox, controls, ordered)}) ranked
         WHERE place <= @limit
         ORDER BY event_id
         LIMIT @candidates
        """;

    /// <summary>
    /// Claims the one delivery <c>@id</c> of the capped subscriber <c>@s0</c>, if it is still claimable — the outer condition
    /// repeats the whole claimability test, as <see cref="ClaimSql"/>'s does.
    /// </summary>
    /// <param name="table">The validated deliveries table name.</param>
    /// <param name="outbox">The validated outbox table name.</param>
    /// <param name="controls">The validated subscriber controls table name.</param>
    /// <param name="ordered">Whether the subscriber's lanes are ordered.</param>
    internal static string ClaimOneSql(string table, string outbox, string controls, bool ordered) =>
        $"""
        UPDATE {table} SET status = 'running', claimed_by = @claimed_by, attempts = attempts + 1, lease_until = @lease_until
         WHERE id = @id AND {OneSubscriber(table, outbox, controls, ordered)}
        RETURNING id, event_id, subscriber, partition_key, attempts
        """;

    private static string OneSubscriber(string table, string outbox, string controls, bool ordered) =>
        $"{Claimable} AND {Registered(1)} AND {NotPaused(table, controls)} AND {LaneHead(table, outbox, ordered ? 1 : 0, ordered ? 0 : 1)}";

    private static string NotPaused(string table, string controls) =>
        $"NOT EXISTS (SELECT 1 FROM {controls} paused WHERE paused.subscriber = {table}.subscriber AND paused.paused_at IS NOT NULL)";

    private static string Registered(int subscribers) => $"subscriber IN ({Parameters(0, subscribers)})";

    private static string LaneHead(string table, string outbox, int ordered, int unordered)
    {
        var noEarlier =
            $"NOT EXISTS (SELECT 1 FROM {table} earlier WHERE earlier.subscriber = {table}.subscriber "
            + $"AND earlier.partition_key = {table}.partition_key AND earlier.event_id < {table}.event_id "
            + $"AND earlier.{LaneBlocking} "
            + "AND (earlier.status IN ('pending', 'running', 'retrying') "
            + "OR (earlier.status = 'dead' AND COALESCE(earlier.last_error_type, '') <> 'unbound')))";
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

    /// <summary>
    /// Records one answered attempt, numbered after the delivery's last recorded one (<c>COALESCE(MAX(attempt), 0) + 1</c>).
    /// </summary>
    /// <remarks>
    /// <b>The number is safe only because the answer's fenced <c>UPDATE</c> of the delivery row runs first, in the same
    /// transaction.</b> On PostgreSQL's <c>READ COMMITTED</c> two concurrent <c>MAX + 1</c> reads would see the same
    /// maximum; what serializes them is the row lock that fenced update takes on the delivery, which every writer of the
    /// delivery's history holds before it inserts here. So the invariant is: a reset, a redeliver or a replay goes through
    /// an <c>UPDATE</c> of the delivery row, and nothing ever writes <c>{attempts}</c> directly.
    /// </remarks>
    internal static string AttemptSql(string attempts) =>
        $"""
        INSERT INTO {attempts} (delivery_id, attempt, started_at, duration_ms, outcome, error_type)
        SELECT @id, COALESCE(MAX(attempt), 0) + 1, @started_at, @duration_ms, @outcome, @error_type FROM {attempts} WHERE delivery_id = @id
        """;

    /// <summary>
    /// Puts a running delivery back to pending, due at <c>@wake_at</c>, and gives back the attempt its claim counted — a
    /// sleep is not an attempt — if the claimant still holds it.
    /// </summary>
    internal static string SleepSql(string table) =>
        $"""
        UPDATE {table} SET status = 'pending', not_before = @wake_at, attempts = attempts - 1,
                           lease_until = NULL, claimed_by = NULL, last_error_type = NULL
         WHERE {Fence}
        """;

    internal static string PendingSubscribersSql(string table) =>
        $"SELECT DISTINCT subscriber FROM {table} WHERE {Unfinished} ORDER BY subscriber";

    internal static string EndPendingSql(string table) =>
        $"UPDATE {table} SET status = @status, last_error_type = @reason, completed_at = @now, lease_until = NULL WHERE subscriber = @subscriber AND {Unfinished}";
}
