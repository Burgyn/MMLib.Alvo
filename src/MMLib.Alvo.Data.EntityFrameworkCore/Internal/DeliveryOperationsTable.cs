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

    internal static string Conditions(DeliveryFilter filter, bool keyset)
    {
        var terms = new List<string>();
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
