namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The operator's two tables — the pause control and the operations log — and, from the next task on, every statement the
/// operator's members of <c>IDeliveryStore</c> run; identical on both shipped engines, as the delivery tables' are.
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
/// record. The key is unique per caller only when one was sent (a partial index).
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
            request_fingerprint TEXT NULL
        )
        """;

    internal static string LogKeyIndexDdl(string log) =>
        $"CREATE UNIQUE INDEX IF NOT EXISTS ux_{log}_key ON {log} (idempotency_scope, idempotency_key) WHERE idempotency_key IS NOT NULL";

    internal static IReadOnlyList<string> Statements(string controls, string log) =>
        [ControlsDdl(controls), LogDdl(log), LogKeyIndexDdl(log)];
}
