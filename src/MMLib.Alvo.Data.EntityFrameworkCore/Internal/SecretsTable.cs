using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Alvo's own secret store: its name, the DDL that creates it, and the four statements
/// <see cref="EfCoreSecretStore"/> reads and writes it with. A framework bookkeeping table like the outbox
/// and idempotency ones, never something the descriptor-diff engine produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is the primary key, and there is no surrogate id.</b> A secret store is a map: one name, one
/// current value. A generated key would make two rows for one name representable, and the store's own
/// contract — a second write replaces the first — would then depend on a <c>DELETE</c> nobody wrote.
/// </para>
/// <para>
/// <b>No <c>SERIAL</c>, no <c>AUTOINCREMENT</c>, no <c>IDENTITY</c></b>, for the reason
/// <see cref="OutboxTable"/> measured: each shipped engine refuses the other's spelling, and SQLite
/// <em>accepts</em> <c>SERIAL</c> as an unrecognised type that silently never increments. One DDL text runs
/// on both engines, which is <see cref="SystemSchemaInitializer"/>'s stated invariant.
/// </para>
/// <para>
/// <b>The value column holds ciphertext, so the table is readable and the secrets are not.</b> An operator
/// listing this table sees names and base64; the key that turns one into a value is a file the platform
/// mounted, which a database dump does not carry.
/// </para>
/// </remarks>
internal static class SecretsTable
{
    /// <summary>The framework's secrets table for a prefix, e.g. <c>alvo_secrets</c>.</summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.SecretsSuffix;

    /// <summary>
    /// The <c>CREATE TABLE IF NOT EXISTS</c> for <paramref name="tableName"/>, safe to run repeatedly.
    /// </summary>
    /// <param name="tableName">The table name, already prefixed by <see cref="NameFor"/>.</param>
    /// <remarks>
    /// The name is a validated identifier assembled from <see cref="AlvoOptions.SchemaPrefix"/>, never
    /// caller-supplied data — and SQL has no bind-parameter form of an identifier, so interpolation is the
    /// only way to place it at all.
    /// </remarks>
    internal static string Ddl(string tableName) =>
        $"""
        CREATE TABLE IF NOT EXISTS {tableName} (
            name TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        )
        """;

    /// <summary>Creates the table if it does not exist yet, outside any transaction.</summary>
    /// <param name="connection">An open connection; opened by the caller, never owned here.</param>
    /// <param name="tableName">The table name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <remarks>
    /// Outside a transaction for the reason <see cref="OutboxTable.EnsureAsync"/> records from measurement:
    /// run inside one, this DDL serialises two concurrent creates on PostgreSQL instead of reaching the
    /// row-level control that is the actual guard.
    /// </remarks>
    internal static async Task EnsureAsync(DbConnection connection, string tableName, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = Ddl(tableName);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one secret's stored value.</summary>
    /// <param name="tableName">The table name, already prefixed by <see cref="NameFor"/>.</param>
    internal static string SelectSql(string tableName) =>
        $"SELECT value FROM {tableName} WHERE name = @name";

    /// <summary>
    /// Writes one secret, replacing whatever the name held.
    /// </summary>
    /// <param name="tableName">The table name, already prefixed by <see cref="NameFor"/>.</param>
    /// <remarks>
    /// <c>INSERT … ON CONFLICT DO UPDATE</c> is one statement on both shipped engines, which is what keeps a
    /// write to the store a single autocommit statement — the constraint <see cref="EfCoreSecretStore"/>'s own
    /// remarks record. A read-then-write pair would be the one shape spike Q5 measured as unretryable under
    /// SQLite WAL.
    /// </remarks>
    internal static string UpsertSql(string tableName) =>
        $"""
        INSERT INTO {tableName} (name, value, updated_at)
        VALUES (@name, @value, @updated_at)
        ON CONFLICT (name) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
        """;

    /// <summary>Removes one secret, reporting whether there was one.</summary>
    /// <param name="tableName">The table name, already prefixed by <see cref="NameFor"/>.</param>
    internal static string DeleteSql(string tableName) => $"DELETE FROM {tableName} WHERE name = @name";

    /// <summary>
    /// Every name the table carries.
    /// </summary>
    /// <param name="tableName">The table name, already prefixed by <see cref="NameFor"/>.</param>
    /// <remarks>
    /// Names only. A listing that selected <c>value</c> would put every secret this deployment holds into one
    /// result set, and the only thing that needs them all at once is an exfiltration.
    /// </remarks>
    internal static string ListNamesSql(string tableName) => $"SELECT name FROM {tableName} ORDER BY name";
}
