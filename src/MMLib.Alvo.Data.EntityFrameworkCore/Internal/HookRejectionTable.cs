using MMLib.Alvo.Rules;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// <c>&lt;prefix&gt;_hook_rejections</c> (HX-09 Ruling 8): one row per rejection by a host write hook — ids and the hook's code,
/// never a payload or a message. Created by <see cref="SystemSchemaInitializer"/> at boot and ensured once per process
/// before an insert.
/// </summary>
/// <remarks>
/// Beyond §4.4.2's column list it carries <c>id</c> (UUIDv7, the primary key) and an index on <c>(extension, at)</c>, the
/// shape the dashboard's per-hook rejection counts read. The longest name here, <c>ix_{prefix}_hook_rejections_extension_at</c>,
/// is at most 48 bytes, inside PostgreSQL's 63-byte identifier limit.
/// </remarks>
internal static class HookRejectionTable
{
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.HookRejectionsSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            id TEXT NOT NULL PRIMARY KEY,
            at TEXT NOT NULL,
            extension TEXT NOT NULL,
            entity TEXT NOT NULL,
            operation TEXT NOT NULL,
            kind TEXT NOT NULL,
            code TEXT NOT NULL,
            actor TEXT NOT NULL
        )
        """;

    internal static string IndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS ix_{table}_extension_at ON {table} (extension, at)";

    /// <summary>Creates the table and its index when missing, autocommit.</summary>
    internal static async Task EnsureAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await IfNotExistsDdl.CreateTableAsync(connection, table, Ddl(table), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, IndexDdl(table), ct).ConfigureAwait(false);
    }

    /// <summary>Inserts <paramref name="rejection"/> on <paramref name="connection"/>, autocommit.</summary>
    internal static Task InsertAsync(DbConnection connection, string table, HookRejection rejection, CancellationToken ct) =>
        ExecutedAsync(connection, InsertSql(table), command => Bind(command, rejection), ct);

    private static string InsertSql(string table) =>
        $"INSERT INTO {table} (id, at, extension, entity, operation, kind, code, actor) VALUES (@id, @at, @extension, @entity, @operation, @kind, @code, @actor)";

    private static void Bind(DbCommand command, HookRejection rejection)
    {
        RelationalSqlBatch.AddParameter(command, "@id", Guid.CreateVersion7(rejection.At).ToString());
        RelationalSqlBatch.AddParameter(command, "@at", StoredInstant.Text(rejection.At));
        RelationalSqlBatch.AddParameter(command, "@extension", rejection.Extension);
        RelationalSqlBatch.AddParameter(command, "@entity", rejection.Entity);
        RelationalSqlBatch.AddParameter(command, "@operation", rejection.Operation);
        RelationalSqlBatch.AddParameter(command, "@kind", rejection.Kind);
        RelationalSqlBatch.AddParameter(command, "@code", rejection.Code);
        RelationalSqlBatch.AddParameter(command, "@actor", rejection.Actor);
    }

    private static async Task ExecutedAsync(DbConnection connection, string sql, Action<DbCommand> bind, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            bind(command);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
