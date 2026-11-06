namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Alvo's data-migration table: one row per started migration, the revision it started after, and when — written in the
/// transaction that appends its run events, so a migration is started once or not at all.
/// </summary>
internal static class DataMigrationTable
{
    /// <summary>The framework's data-migration table for a prefix, e.g. <c>alvo_data_migrations</c>.</summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.DataMigrationsSuffix;

    /// <summary>The <c>CREATE TABLE IF NOT EXISTS</c> for <paramref name="table"/>.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            id TEXT NOT NULL PRIMARY KEY,
            revision INTEGER NOT NULL,
            started_at TEXT NOT NULL
        )
        """;

    /// <summary>Marks a migration started; does nothing when it was already, at any revision.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string StartSql(string table) =>
        $"INSERT INTO {table} (id, revision, started_at) VALUES (@id, @revision, @started_at) ON CONFLICT (id) DO NOTHING";

    /// <summary>Reads every started migration's id, in ordinal order.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string StartedSql(string table) => $"SELECT id FROM {table} ORDER BY id";
}
