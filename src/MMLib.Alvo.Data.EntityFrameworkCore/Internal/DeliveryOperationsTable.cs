using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The operator's two tables — the pause control and the operations log — and every statement the operator's members of
/// <c>IDeliveryStore</c> run; identical on both shipped engines, as the delivery tables' are.
/// </summary>
/// <remarks>
/// <para>
/// <b>The control table carries the spec's whole row now</b>, <c>disabled_at</c>/<c>disabled_by</c> included, although
/// nothing writes them until validate hooks can be switched off: a column added later is an <c>ALTER</c> of a table every
/// running database already holds, which the framework's tables never take (spec E10).
/// </para>
/// <para>
/// <b>The log carries the replay's idempotency record.</b> Beside E11's six columns, a replay files how many deliveries it
/// reset, the first ids, and the caller's key, scope and request fingerprint in the same row, written in the same
/// transaction as the reset — so a retried replay is answered from the row, and a crash cannot leave a reset without its
/// record. The key is unique per caller only when one was sent (a partial index), and a <c>CHECK</c> refuses a key stored
/// without its scope: SQL treats <c>NULL</c>s as distinct, so a scope-less key would slip past the unique index.
/// </para>
/// <para>
/// <b>A page seeks, it does not sort.</b> A page orders by the primary key, newest first, and continues with
/// <c>id &lt; @after</c>, so an unfiltered page walks the primary key backwards and stops at the limit. A page of one
/// subscriber — the Operations page's common filter — seeks <c>ix_{deliveries}_subscriber_id</c> on
/// <c>(subscriber, id)</c> the same way. A page filtered by status alone is served either by the claim index on
/// <c>(status, not_before)</c> followed by a sort, or by the same backwards primary-key walk discarding other statuses; the
/// engine picks. A rare status (<c>dead</c>) costs at most one pass over the table, bounded by the store's page ceiling.
/// </para>
/// <para>
/// <b>Identifier length.</b> <see cref="AlvoOptions.SchemaPrefix"/> is capped at 16 characters, so the longest name here,
/// <c>ix_{prefix}_deliveries_subscriber_id</c>, is at most 44 bytes, inside PostgreSQL's 63-byte identifier limit.
/// </para>
/// </remarks>
internal static class DeliveryOperationsTable
{
    internal static string ControlsNameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.SubscriberControlsSuffix;

    internal static string LogNameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.OperationsLogSuffix;

    internal static string ControlsDdl(string controls) =>
        $"""
        CREATE TABLE IF NOT EXISTS {controls} (
            subscriber TEXT NOT NULL PRIMARY KEY,
            paused_at TEXT NULL,
            paused_by TEXT NULL,
            reason TEXT NULL,
            disabled_at TEXT NULL,
            disabled_by TEXT NULL
        )
        """;

    internal static string LogDdl(string log) =>
        $"""
        CREATE TABLE IF NOT EXISTS {log} (
            id TEXT NOT NULL PRIMARY KEY,
            at TEXT NOT NULL,
            actor TEXT NOT NULL,
            operation TEXT NOT NULL,
            target TEXT NOT NULL,
            reason TEXT NULL,
            affected INTEGER NULL,
            sample TEXT NULL,
            idempotency_scope TEXT NULL,
            idempotency_key TEXT NULL,
            request_fingerprint TEXT NULL,
            CHECK (idempotency_key IS NULL OR idempotency_scope IS NOT NULL)
        )
        """;

    internal static string LogKeyIndexDdl(string log) =>
        $"CREATE UNIQUE INDEX IF NOT EXISTS ux_{log}_key ON {log} (idempotency_scope, idempotency_key) WHERE idempotency_key IS NOT NULL";

    internal static string SubscriberPageIndexDdl(string deliveries) =>
        $"CREATE INDEX IF NOT EXISTS ix_{deliveries}_subscriber_id ON {deliveries} (subscriber, id)";

    internal static IReadOnlyList<string> Statements(string deliveries, string controls, string log) =>
        [SubscriberPageIndexDdl(deliveries), ControlsDdl(controls), LogDdl(log), LogKeyIndexDdl(log)];

    internal const int MaxPage = 1000;

    internal const string DeliveryColumns =
        "id, event_id, subscriber, partition_key, tenant_id, kind, status, attempts, not_before, completed_at, last_error_type";

    internal static string ListSql(string table, DeliveryFilter filter) =>
        $"SELECT {DeliveryColumns} FROM {table}{Where(filter, keyset: true)} ORDER BY id DESC LIMIT @limit";

    internal static string FindSql(string table) => $"SELECT {DeliveryColumns} FROM {table} WHERE id = @id";

    internal static string AttemptsSql(string attempts) =>
        $"SELECT attempt, started_at, duration_ms, outcome, error_type FROM {attempts} WHERE delivery_id = @id ORDER BY attempt";

    internal const string ReplayableTerm =
        "status IN ('succeeded', 'dead', 'cancelled') AND COALESCE(last_error_type, '') <> 'chain-depth-exceeded'";

    /// <summary>Whether a delivery of the row's subscriber and partition key is running — the row's lane is busy.</summary>
    internal static string LaneRunning(string table) =>
        $"EXISTS (SELECT 1 FROM {table} lane WHERE lane.subscriber = {table}.subscriber AND lane.partition_key = {table}.partition_key AND lane.status = 'running')";

    /// <summary>
    /// The count, the busy count and the sample in one statement: the window aggregates are taken before the limit, so they
    /// cannot disagree with the sample, and a filter matching nothing returns no row — counts of zero.
    /// </summary>
    /// <remarks>
    /// The exact count reads every matching row, and the busy count probes each one's lane, before the limit applies: the
    /// cost grows with the selection, not with the sample. A replay is bounded by its ceiling of 10 000 deliveries per real
    /// run, so a selection worth acting on stays within that many rows; a wider one is read in full to be refused.
    /// </remarks>
    internal static string SelectSql(string table, DeliveryFilter filter) =>
        $"SELECT id, COUNT(*) OVER (), SUM(CASE WHEN {LaneRunning(table)} THEN 1 ELSE 0 END) OVER () FROM {table}{Where(filter, keyset: false)} ORDER BY event_id, id LIMIT @limit";

    internal const string LogColumns =
        "id, at, actor, operation, target, reason, affected, sample, idempotency_scope, idempotency_key, request_fingerprint";

    internal const string Finished = "status IN ('succeeded', 'dead', 'cancelled')";

    internal const string Reset =
        "status = 'pending', attempts = 0, not_before = @now, lease_until = NULL, claimed_by = NULL, completed_at = NULL, last_error_type = NULL";

    /// <summary>
    /// Puts one finished delivery whose lane runs nothing back to pending. An <c>UPDATE</c> of the delivery row, never a
    /// write to the attempts table: on PostgreSQL the row lock it takes is what orders it against a fenced answer, and the
    /// history keeps numbering after the reset (<see cref="DeliveryTable.AttemptSql"/>).
    /// </summary>
    /// <remarks>
    /// The <c>NOT EXISTS</c> lane check alone does not hold on PostgreSQL: under READ COMMITTED it reads the statement's
    /// snapshot and is not re-checked when a claim commits meanwhile, so a claim of a later delivery in the lane — ordered
    /// or unordered — could pass beside the reset. The store runs this statement after taking the subscriber's lock
    /// exclusively, as the claim takes it shared (<see cref="SubscriberLockKey"/>), so one of the two starts only after the
    /// other committed.
    /// </remarks>
    internal static string RedeliverSql(string table) =>
        $"UPDATE {table} SET {Reset} WHERE id = @id AND {Finished} AND NOT {LaneRunning(table)}";

    /// <summary>
    /// Puts every replayable delivery <paramref name="filter"/> selects whose lane runs nothing back to pending, and reads
    /// back what it reset — the <see cref="SelectSql"/> predicate, less the busy rows, so a dry run counts what a run resets.
    /// </summary>
    /// <remarks>
    /// Run after the subscribers' locks are taken exclusively, as <see cref="RedeliverSql"/> is. A filter naming no
    /// subscriber is narrowed to the <paramref name="lockedSubscribers"/> the store read with
    /// <see cref="ReplaySubscribersSql"/> and locked, bound as <c>@replay_subscriber{n}</c>: a subscriber whose first
    /// replayable row arrived after that read was not locked, so its rows are never reset beside a claim. Such a filter
    /// with no locked subscriber is refused rather than rendered unnarrowed.
    /// </remarks>
    internal static string ReplaySql(string table, DeliveryFilter filter, int lockedSubscribers = 0) =>
        $"UPDATE {table} SET {Reset} WHERE {Conditions(filter with { Replayable = true }, keyset: false)}{LockedSubscribers(filter, lockedSubscribers)} AND NOT {LaneRunning(table)} RETURNING id, event_id";

    /// <summary>The subscribers with a replayable delivery <paramref name="filter"/> selects — the locks a replay naming none takes.</summary>
    internal static string ReplaySubscribersSql(string table, DeliveryFilter filter) =>
        $"SELECT DISTINCT subscriber FROM {table} WHERE {Conditions(filter with { Replayable = true }, keyset: false)}";

    internal static string LogOutcomeSql(string log) => $"UPDATE {log} SET affected = @log_affected, sample = @log_sample WHERE id = @log_id";

    internal static string FindLogSql(string log) =>
        $"SELECT {LogColumns} FROM {log} WHERE idempotency_scope = @scope AND idempotency_key = @key";

    private static string LockedSubscribers(DeliveryFilter filter, int count)
    {
        if (filter.Subscriber is not null)
        {
            return string.Empty;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        return $" AND subscriber IN ({string.Join(", ", Enumerable.Range(0, count).Select(index => $"@replay_subscriber{index}"))})";
    }

    internal static string InsertLogSql(string log) =>
        $"""
        INSERT INTO {log} ({LogColumns})
        VALUES (@log_id, @log_at, @log_actor, @log_operation, @log_target, @log_reason, @log_affected, @log_sample, @log_scope, @log_key, @log_fingerprint)
        ON CONFLICT DO NOTHING
        """;

    internal static string AllOperationsSql(string log) => $"SELECT {LogColumns} FROM {log} ORDER BY at, id";

    internal static string SubscriberOfSql(string table) => $"SELECT subscriber FROM {table} WHERE id = @id";

    /// <summary>
    /// The key of a subscriber's transaction lock (<see cref="IAlvoSqlDialect.TransactionLockStatement"/>): the table is
    /// part of it, so two Alvo schemas in one database never wait on each other.
    /// </summary>
    internal static long SubscriberLockKey(string table, string subscriber) => LockKey($"{table}:{subscriber}");

    /// <summary>
    /// FNV-1a, 64-bit, over the UTF-8 of <paramref name="text"/> — computed here rather than by the server so the key
    /// depends on the name alone: every process, every server version and every engine derives the same one. A collision
    /// with another key only makes two takers wait on each other; it never lets a conflict through.
    /// </summary>
    internal static long LockKey(string text)
    {
        const ulong Prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        foreach (var octet in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash = unchecked((hash ^ octet) * Prime);
        }

        return unchecked((long)hash);
    }

    internal static string Conditions(DeliveryFilter filter, bool keyset)
    {
        var terms = new List<string>();
        AddWhen(terms, filter.Replayable, ReplayableTerm);
        AddWhen(terms, filter.Subscriber is not null, "subscriber = @subscriber");
        AddWhen(terms, filter.PartitionKey is not null, "partition_key = @partition_key");
        AddWhen(terms, filter.Status is not null, "status = @status");
        AddWhen(terms, filter.FromEventId is not null, "event_id >= @from_event");
        AddWhen(terms, filter.ToEventId is not null, "event_id < @to_event");
        AddWhen(terms, keyset && filter.After is not null, "id < @after");

        return string.Join(" AND ", terms);
    }

    private static string Where(DeliveryFilter filter, bool keyset) =>
        Conditions(filter, keyset) is { Length: > 0 } terms ? $" WHERE {terms}" : string.Empty;

    private static void AddWhen(List<string> terms, bool present, string term)
    {
        if (present)
        {
            terms.Add(term);
        }
    }
}
