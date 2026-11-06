namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Alvo's schedule table: one row per registered cron schedule, its next occurrence, the lease and claimant of the
/// occurrence in flight, and the last occurrence that ran. Identical SQL on both shipped engines; every instant is
/// <see cref="StoredInstant.Text"/>.
/// </summary>
/// <remarks>
/// An advance is fenced twice: by <c>leased_by</c>, so a claimant whose lease ran out and was taken over changes nothing,
/// and by <c>next_run_at</c>, so an occurrence a sync moved meanwhile is not run under its old time. A schedule's last
/// outcome is not stored here; it is the newest delivery of its run event.
/// </remarks>
internal static class ScheduleTable
{
    /// <summary>
    /// When an occurrence may be taken: due, and held by no lease still running. One constant in both halves of
    /// <see cref="ClaimSql"/>, for the EvalPlanQual reason <see cref="TimerTable.ClaimSql"/> records.
    /// </summary>
    private const string Claimable = "(next_run_at <= @now AND (lease_until IS NULL OR lease_until <= @now))";

    /// <summary>The framework's schedule table for a prefix, e.g. <c>alvo_schedules</c>.</summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.SchedulesSuffix;

    /// <summary>The <c>CREATE TABLE IF NOT EXISTS</c> for <paramref name="table"/>.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            id TEXT NOT NULL PRIMARY KEY,
            cron TEXT NOT NULL,
            time_zone TEXT NOT NULL,
            next_run_at TEXT NOT NULL,
            lease_until TEXT NULL,
            leased_by TEXT NULL,
            last_run_at TEXT NULL
        )
        """;

    /// <summary>Inserts a new schedule, or moves the next run of one whose cron or time zone changed; an unchanged one keeps its.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string SyncSql(string table) =>
        $"""
        INSERT INTO {table} (id, cron, time_zone, next_run_at) VALUES (@id, @cron, @time_zone, @next_run_at)
        ON CONFLICT (id) DO UPDATE SET cron = excluded.cron, time_zone = excluded.time_zone, next_run_at = excluded.next_run_at
         WHERE {table}.cron <> excluded.cron OR {table}.time_zone <> excluded.time_zone
        """;

    /// <summary>Deletes the <paramref name="retired"/> declared-retired ids, bound as <c>@retired0</c>, <c>@retired1</c>, ….</summary>
    /// <param name="table">The validated table name.</param>
    /// <param name="retired">How many ids are bound; at least one.</param>
    internal static string RetireSql(string table, int retired) =>
        $"DELETE FROM {table} WHERE id IN ({Placeholders("@retired", retired)})";

    /// <summary>Leases every claimable occurrence among the <paramref name="ids"/> bound ids (<c>@id0</c>, …) for <c>@claimant</c>.</summary>
    /// <param name="table">The validated table name.</param>
    /// <param name="ids">How many ids are bound; at least one.</param>
    /// <remarks>The outer <c>WHERE</c> repeats <see cref="Claimable"/>: see <see cref="TimerTable.ClaimSql"/>.</remarks>
    internal static string ClaimSql(string table, int ids) =>
        $"""
        UPDATE {table} SET lease_until = @lease_until, leased_by = @claimant
         WHERE {Claimable}
           AND id IN (SELECT id FROM {table} WHERE {Claimable} AND id IN ({Placeholders("@id", ids)}) ORDER BY next_run_at, id)
        RETURNING id, next_run_at
        """;

    private static string Placeholders(string prefix, int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(index => $"{prefix}{index}"));

    /// <summary>Moves one leased occurrence to the next run and records it as the last, releasing the lease.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string AdvanceSql(string table) =>
        $"""
        UPDATE {table} SET next_run_at = @next_run_at, last_run_at = @scheduled_for, lease_until = NULL, leased_by = NULL
         WHERE id = @id AND leased_by = @claimant AND next_run_at = @scheduled_for
        """;

    /// <summary>Reads every schedule, by id.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string AllSql(string table) =>
        $"SELECT id, cron, time_zone, next_run_at, last_run_at FROM {table}";
}
