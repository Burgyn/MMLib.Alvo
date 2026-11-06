namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Alvo's timer table: one row per armed timer, keyed by scope and key, fenced by a generation every upsert mints afresh.
/// Identical SQL on both shipped engines; every instant is <see cref="StoredInstant.Text"/>, whose UTC text sorts in
/// instant order.
/// </summary>
/// <remarks>
/// The key column is <c>timer_key</c>, not <c>key</c>, which T-SQL reserves. The bind-parameter names are not in
/// <see cref="PolicyParameterPrefix"/> for the reason <see cref="OutboxTable"/> records: hand-written, single-fragment
/// statements over no entity table.
/// </remarks>
internal static class TimerTable
{
    /// <summary>
    /// When a timer may be taken: due, and held by no lease that is still running. One constant in both halves of
    /// <see cref="ClaimSql"/>, for the reason its remarks give.
    /// </summary>
    private const string Claimable = "(due_at <= @now AND (lease_until IS NULL OR lease_until <= @now))";

    /// <summary>The framework's timer table for a prefix, e.g. <c>alvo_timers</c>.</summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.TimersSuffix;

    /// <summary>The <c>CREATE TABLE IF NOT EXISTS</c> for <paramref name="table"/>.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            scope TEXT NOT NULL,
            timer_key TEXT NOT NULL,
            generation TEXT NOT NULL,
            due_at TEXT NOT NULL,
            message_type TEXT NOT NULL,
            payload TEXT NOT NULL,
            provenance TEXT NOT NULL,
            lease_until TEXT NULL,
            PRIMARY KEY (scope, timer_key)
        )
        """;

    /// <summary>The index a claim and a due count seek by.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string DueIndexDdl(string table) => $"CREATE INDEX IF NOT EXISTS ix_{table}_due ON {table} (due_at)";

    /// <summary>Arms a timer, replacing one of the same scope and key with a new generation, due time and message, unleased.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string UpsertSql(string table) =>
        $"""
        INSERT INTO {table} (scope, timer_key, generation, due_at, message_type, payload, provenance, lease_until)
        VALUES (@scope, @key, @generation, @due_at, @message_type, @payload, @provenance, NULL)
        ON CONFLICT (scope, timer_key) DO UPDATE SET
            generation = excluded.generation, due_at = excluded.due_at, message_type = excluded.message_type,
            payload = excluded.payload, provenance = excluded.provenance, lease_until = NULL
        """;

    /// <summary>Removes the timer of one scope and key, whatever its generation.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string CancelSql(string table) => $"DELETE FROM {table} WHERE scope = @scope AND timer_key = @key";

    /// <summary>Leases up to <c>@batch</c> claimable timers, the earliest due first, and returns them.</summary>
    /// <param name="table">The validated table name.</param>
    /// <remarks>
    /// <para>
    /// <b>The outer <c>WHERE</c> repeats <see cref="Claimable"/>, and that is the statement's correctness.</b> Under
    /// PostgreSQL's <c>READ COMMITTED</c>, a claimant blocked on a row another claimant is leasing re-checks only the outer
    /// predicate (EvalPlanQual) against the newer row, never the subquery — so with the predicate in the subquery alone it
    /// would re-lease what the winner just took. With it repeated, the loser sees <c>lease_until &gt; @now</c> and skips the
    /// row. This is the outbox's measured argument (<see cref="OutboxTable.ClaimSql"/>), verbatim.
    /// </para>
    /// <para>
    /// An upsert clears <c>lease_until</c>, so a timer moved while a claim of it runs is claimable at its new time; the old
    /// claim's fire then finds a different generation and deletes nothing. <c>RETURNING</c>'s order is arbitrary on both
    /// engines, so the store sorts what it read.
    /// </para>
    /// </remarks>
    internal static string ClaimSql(string table) =>
        $"""
        UPDATE {table} SET lease_until = @lease_until
         WHERE {Claimable}
           AND (scope, timer_key) IN (SELECT scope, timer_key FROM {table}
                                       WHERE {Claimable}
                                       ORDER BY due_at, scope, timer_key
                                       LIMIT @batch)
        RETURNING scope, timer_key, generation, due_at, message_type, payload, provenance
        """;

    /// <summary>Retires one timer, only at the generation its claim read.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string FireSql(string table) =>
        $"DELETE FROM {table} WHERE scope = @scope AND timer_key = @key AND generation = @generation";

    /// <summary>Counts every timer, and those due at <c>@now</c>.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string CountSql(string table) =>
        $"SELECT COUNT(*), COALESCE(SUM(CASE WHEN due_at <= @now THEN 1 ELSE 0 END), 0) FROM {table}";
}
