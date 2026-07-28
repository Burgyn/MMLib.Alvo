using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Alvo's own idempotency-record table: the name, the DDL, and the two statements the write path runs
/// against it. A framework bookkeeping table like the descriptor-versions one, not something the
/// descriptor-diff engine produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the name lives in one static member.</b> Three unrelated pieces of code have to agree on it and
/// none can see the others: <see cref="SystemSchemaInitializer"/> creates it,
/// <see cref="EfCoreSchemaIntrospector"/> must <em>exclude</em> it (a table it reported as the user's schema
/// would be planned for a <c>DROP</c> on the next re-apply), and this type reads and writes it. That is
/// exactly the arrangement <see cref="SystemSchemaInitializer.DescriptorVersionsTableName"/> already
/// established, and the precedent is followed rather than re-derived.
/// </para>
/// <para>
/// <b>The DDL is identical on both engines</b> — one <c>CREATE TABLE IF NOT EXISTS</c> over ANSI-portable
/// column types — so nothing here branches per engine. <c>row_id</c> and <c>created_at</c> are <c>TEXT</c>
/// for the same reason the versions table's <c>created_at</c> is: this table is never filtered, sorted or
/// joined on, so a portable spelling costs nothing and a per-engine type mapping would have to be maintained
/// by every future driver.
/// </para>
/// <para>
/// <b>The record stores a row id, not a response body.</b> A replay re-reads the row through the caller's
/// current policy, so it can never hand back a representation that policy would not produce today. Caching
/// the body would also make this table grow with payload size and require an eviction rule nobody has
/// designed.
/// </para>
/// <para>
/// <b><c>tenant_id</c> is part of the primary key, not a column beside it.</b> A key is the caller's own
/// string, so two tenants will collide on <c>"1"</c> sooner rather than later; in a shared key space one
/// tenant's replay would be answered with another tenant's row id. A global entity has no tenant, so
/// <see cref="TenantKey"/> substitutes a fixed sentinel and the column stays <c>NOT NULL</c> — the
/// alternative, a nullable column in a primary key, is not portable and compares as <c>UNKNOWN</c> anyway.
/// </para>
/// <para>
/// <b>Its bind-parameter names are not in <see cref="PolicyParameterPrefix"/>.</b> That registry exists
/// because one composed read statement carries fragments from several renderers that never see each other's
/// output; these two statements are hand-written, single-fragment, and touch no entity table, so there is no
/// second contributor a name could collide with.
/// </para>
/// </remarks>
internal static class IdempotencyTable
{
    /// <summary>The framework's idempotency table for a prefix, e.g. <c>alvo_idempotency</c>.</summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    internal static string NameFor(string schemaPrefix) => $"{schemaPrefix}_idempotency";

    /// <summary>
    /// The <c>CREATE TABLE IF NOT EXISTS</c> for <paramref name="tableName"/>, safe to run repeatedly.
    /// </summary>
    /// <param name="tableName">The table name, already prefixed by <see cref="NameFor"/>.</param>
    /// <remarks>
    /// The name is a validated identifier assembled from <see cref="AlvoOptions.SchemaPrefix"/> (see
    /// <see cref="SystemSchemaInitializer"/>'s constructor guard), never caller-supplied data — and SQL has
    /// no bind-parameter form of an identifier, so interpolation is the only way to place it at all.
    /// </remarks>
    internal static string Ddl(string tableName) =>
        $"""
        CREATE TABLE IF NOT EXISTS {tableName} (
            key TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            entity TEXT NOT NULL,
            row_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (key, tenant_id)
        )
        """;

    /// <summary>
    /// The tenant half of a record's identity: the caller's tenant, or a fixed sentinel when they have none.
    /// </summary>
    /// <param name="context">The caller the create is performed as.</param>
    /// <remarks>
    /// The all-zero <see cref="Guid"/> is already reserved framework-wide to mean "no identity" (see
    /// <see cref="UserId"/>'s own remarks), so reusing it here introduces no new convention. A real tenant can
    /// never be the empty <see cref="Guid"/>, so the sentinel cannot collide with one.
    /// </remarks>
    internal static string TenantKey(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.Tenant?.Value ?? Guid.Empty).ToString();
    }

    /// <summary>
    /// The record stored for one key in one tenant, or <see langword="null"/> when the key is unused there.
    /// </summary>
    /// <param name="Fingerprint">The fingerprint of the request the key was first used for.</param>
    /// <param name="RowId">The id of the row that first request created.</param>
    internal readonly record struct IdempotencyRecord(string Fingerprint, Guid RowId);

    /// <summary>
    /// Creates the table inside <paramref name="transaction"/> if it does not exist yet.
    /// </summary>
    /// <param name="connection">The write transaction's own connection.</param>
    /// <param name="transaction">The in-flight write transaction.</param>
    /// <param name="tableName">The table name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <remarks>
    /// Run per idempotent create rather than memoized once per process, deliberately. A memo set inside a
    /// transaction that later rolls back is a lie — both engines roll DDL back with everything else — and the
    /// alternative, creating the table on a second connection outside the transaction, would give the table two
    /// creators and, on SQLite, a second writer contending with the transaction that is about to need it. An
    /// existing table makes this a schema lookup, and it is only reached by a create that carries a token.
    /// </remarks>
    internal static async Task EnsureAsync(
        DbConnection connection, DbTransaction transaction, string tableName, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = Ddl(tableName);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the record for one key in one tenant, inside the caller's transaction.</summary>
    /// <param name="connection">The write transaction's own connection.</param>
    /// <param name="transaction">The in-flight write transaction, so the read cannot see an uncommitted rival.</param>
    /// <param name="tableName">The table name.</param>
    /// <param name="key">The caller's key, bound as a value.</param>
    /// <param name="tenantKey">The tenant half of the record's identity, from <see cref="TenantKey"/>.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal static async Task<IdempotencyRecord?> FindAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string key,
        string tenantKey,
        CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT fingerprint, row_id FROM {tableName} WHERE key = @key AND tenant_id = @tenant_id";
            RelationalSqlBatch.AddParameter(command, "@key", key);
            RelationalSqlBatch.AddParameter(command, "@tenant_id", tenantKey);

            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(ct).ConfigureAwait(false)
                    ? new IdempotencyRecord(reader.GetString(0), Guid.Parse(reader.GetString(1)))
                    : null;
            }
        }
    }

    /// <summary>
    /// Records that <paramref name="token"/>'s key created <paramref name="rowId"/>, in the same transaction
    /// as the row itself — so the record and the row commit together or not at all.
    /// </summary>
    /// <param name="connection">The write transaction's own connection.</param>
    /// <param name="transaction">The in-flight write transaction.</param>
    /// <param name="tableName">The table name.</param>
    /// <param name="token">The caller's idempotency token.</param>
    /// <param name="tenantKey">The tenant half of the record's identity, from <see cref="TenantKey"/>.</param>
    /// <param name="entity">The entity the row belongs to, recorded for diagnostics only — never for lookup.</param>
    /// <param name="rowId">The id of the row this create inserted.</param>
    /// <param name="createdAt">The instant the record is written, from the framework's own clock.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <remarks>
    /// A duplicate primary key here is the whole concurrency control: two requests carrying one key can both
    /// find no record and both insert a row, and this insert is what makes exactly one of them commit.
    /// </remarks>
    internal static async Task InsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        AlvoIdempotency token,
        string tenantKey,
        string entity,
        Guid rowId,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText =
                $"""
                INSERT INTO {tableName} (key, tenant_id, fingerprint, entity, row_id, created_at)
                VALUES (@key, @tenant_id, @fingerprint, @entity, @row_id, @created_at)
                """;
            RelationalSqlBatch.AddParameter(command, "@key", token.Key);
            RelationalSqlBatch.AddParameter(command, "@tenant_id", tenantKey);
            RelationalSqlBatch.AddParameter(command, "@fingerprint", token.Fingerprint);
            RelationalSqlBatch.AddParameter(command, "@entity", entity);
            RelationalSqlBatch.AddParameter(command, "@row_id", rowId.ToString());
            RelationalSqlBatch.AddParameter(
                command, "@created_at", createdAt.ToString("O", CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
