namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Alvo's step table: one row per completed step of one subscriber's delivery of one event, keyed by (event, subscriber,
/// step). Identical SQL on both shipped engines; every instant is <see cref="StoredInstant.Text"/>.
/// </summary>
/// <remarks>
/// <b>The first writer wins.</b> A record is an <c>INSERT … ON CONFLICT DO NOTHING</c> followed by a read of the key, so
/// two runners that raced one step both continue with the result that was stored first. On PostgreSQL a concurrent insert
/// of the same key waits on the primary key's index, then does nothing.
/// </remarks>
internal static class StepTable
{
    /// <summary>The framework's step table for a prefix, e.g. <c>alvo_steps</c>.</summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.StepsSuffix;

    /// <summary>The <c>CREATE TABLE IF NOT EXISTS</c> for <paramref name="table"/>.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            event_id TEXT NOT NULL,
            subscriber TEXT NOT NULL,
            step TEXT NOT NULL,
            result TEXT NOT NULL,
            completed_at TEXT NOT NULL,
            PRIMARY KEY (event_id, subscriber, step)
        )
        """;

    /// <summary>Records one step unless its key is already recorded.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string RecordSql(string table) =>
        $"""
        INSERT INTO {table} (event_id, subscriber, step, result, completed_at)
        VALUES (@event_id, @subscriber, @step, @result, @completed_at)
        ON CONFLICT (event_id, subscriber, step) DO NOTHING
        """;

    /// <summary>Reads the stored result and instant of one step.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string ReadOneSql(string table) =>
        $"SELECT result, completed_at FROM {table} WHERE event_id = @event_id AND subscriber = @subscriber AND step = @step";

    /// <summary>
    /// Deletes the steps of the delivery <c>@id</c> while it is <c>succeeded</c> — run first in a redeliver's transaction,
    /// which is rolled back when its reset then matches nothing.
    /// </summary>
    /// <param name="table">The validated step table name.</param>
    /// <param name="deliveries">The validated delivery table name.</param>
    internal static string ClearSucceededSql(string table, string deliveries) =>
        $"DELETE FROM {table} WHERE EXISTS (SELECT 1 FROM {deliveries} WHERE {deliveries}.id = @id AND {deliveries}.status = 'succeeded' "
        + $"AND {deliveries}.event_id = {table}.event_id AND {deliveries}.subscriber = {table}.subscriber)";

    /// <summary>
    /// Deletes the steps of every succeeded delivery a replay may reset — <paramref name="selected"/>, the replay's filter
    /// over <paramref name="deliveries"/> (<see cref="DeliveryOperationsTable.ClearedOnReplaySql"/>) — run in the replay's
    /// transaction just before the reset.
    /// </summary>
    /// <remarks>
    /// The deliveries table is aliased <c>replayed</c> and <paramref name="selected"/> names delivery columns unqualified:
    /// every column it names exists on the deliveries table, so each binds to the innermost scope, never to the step table
    /// that shares <c>event_id</c> and <c>subscriber</c> with it.
    /// </remarks>
    /// <param name="table">The validated step table name.</param>
    /// <param name="deliveries">The validated delivery table name.</param>
    /// <param name="selected">The replay's filter, over unqualified delivery columns.</param>
    internal static string ClearReplayedSql(string table, string deliveries, string selected) =>
        $"DELETE FROM {table} WHERE EXISTS (SELECT 1 FROM {deliveries} replayed WHERE {selected} AND replayed.status = 'succeeded' "
        + $"AND replayed.event_id = {table}.event_id AND replayed.subscriber = {table}.subscriber)";

    /// <summary>Reads every step of one subscriber's delivery of one event, in the order they completed.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string ReadSql(string table) =>
        $"SELECT step, result, completed_at FROM {table} WHERE event_id = @event_id AND subscriber = @subscriber ORDER BY completed_at, step";
}
