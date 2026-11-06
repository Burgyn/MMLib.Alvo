using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The framework's counter table, <c>&lt;prefix&gt;_sequences</c>: one row per sequence count — entity, field, tenant,
/// <c>per</c> value and period — holding the last number it issued.
/// </summary>
/// <remarks>
/// <b>Counter rows are never deleted</b>: removing and re-adding a sequence resumes its count, and a rollback never
/// re-issues a number. A declared rename of the entity or the field rewrites its rows' keys inside the apply's own
/// transaction, so a renamed sequence keeps counting instead of restarting at its start.
/// </remarks>
internal static class SequenceTable
{
    /// <summary>The counter table's name under <paramref name="schemaPrefix"/>.</summary>
    /// <param name="schemaPrefix">The configured schema prefix.</param>
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.SequencesSuffix;

    /// <summary>The table's idempotent DDL, the same text on both shipped engines.</summary>
    /// <param name="table">The prefixed table name.</param>
    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            entity TEXT NOT NULL,
            field TEXT NOT NULL,
            tenant_key TEXT NOT NULL,
            scope_key TEXT NOT NULL,
            period_key TEXT NOT NULL,
            last_value BIGINT NOT NULL,
            PRIMARY KEY (entity, field, tenant_key, scope_key, period_key)
        )
        """;

    /// <summary>The single-statement allocation both shipped engines answer <c>SequenceAllocationStatement</c> with.</summary>
    /// <param name="table">The prefixed table name.</param>
    internal static string Upsert(string table) =>
        $"""
        INSERT INTO {table} (entity, field, tenant_key, scope_key, period_key, last_value)
        VALUES (@entity, @field, @tenant, @scope, @period, @start)
        ON CONFLICT (entity, field, tenant_key, scope_key, period_key)
        DO UPDATE SET last_value = {table}.last_value + 1
        RETURNING last_value
        """;

    /// <summary>Moves every count of entity <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <param name="table">The prefixed table name.</param>
    /// <param name="from">The entity's previous name.</param>
    /// <param name="to">The entity's new name.</param>
    internal static string RenameEntity(string table, string from, string to) =>
        $"UPDATE {table} SET entity = {Literal(to)} WHERE entity = {Literal(from)}";

    /// <summary>Moves every count of field <paramref name="from"/> of <paramref name="entity"/> to <paramref name="to"/>.</summary>
    /// <param name="table">The prefixed table name.</param>
    /// <param name="entity">The entity, under its new name.</param>
    /// <param name="from">The field's previous name.</param>
    /// <param name="to">The field's new name.</param>
    internal static string RenameField(string table, string entity, string from, string to) =>
        $"UPDATE {table} SET field = {Literal(to)} WHERE entity = {Literal(entity)} AND field = {Literal(from)}";

    /// <summary>Creates the table when it does not exist; safe against a rival creator.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">The prefixed table name.</param>
    /// <param name="ct">A token to cancel the statement.</param>
    internal static Task EnsureAsync(DbConnection connection, string table, CancellationToken ct) =>
        IfNotExistsDdl.CreateTableAsync(connection, table, Ddl(table), ct);

    private static string Literal(string name) =>
        AlvoSqlStringLiteral.TryQuote(name, out var literal)
            ? literal
            : throw new InvalidOperationException($"'{name}' cannot be written as a SQL literal, so its sequence counter cannot be renamed.");
}
