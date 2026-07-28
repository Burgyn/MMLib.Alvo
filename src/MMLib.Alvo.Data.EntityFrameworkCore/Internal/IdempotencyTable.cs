using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Alvo's own idempotency-record table: its name, the DDL that creates it, and the two statements the write
/// path reads and writes a record with. A framework bookkeeping table like the descriptor-versions one, not
/// something the descriptor-diff engine produces.
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
/// <b>The record's identity is not decided here.</b> The key's scope — the tenant and the acting user — comes
/// from <see cref="AlvoIdempotency.IdentityOf"/> on the port, and the fingerprint comparison from
/// <see cref="AlvoIdempotency.Matches"/>, because the reference implementation has to answer both questions
/// identically and neither can see the other. This type stores what it is given.
/// </para>
/// <para>
/// <b>The DDL is identical on both shipped engines</b> — one <c>CREATE TABLE IF NOT EXISTS</c> over
/// ANSI-portable column types — so nothing here branches per engine. <c>row_id</c> and <c>created_at</c> are
/// <c>TEXT</c> for the same reason the versions table's <c>created_at</c> is: this table is never filtered,
/// sorted or joined on, so a portable spelling costs nothing and a per-engine type mapping would have to be
/// maintained by every future driver. The portability claim is scoped to SQLite and PostgreSQL: on T-SQL
/// <c>TEXT</c> is deprecated and would need <c>nvarchar</c>, which is follow-up work for whoever writes that
/// driver rather than a mapping invented here for a driver nobody is writing (see
/// <c>docs/architecture/data-path.md</c>). The <c>idempotency_key</c> column is <em>not</em> named <c>key</c>
/// for the same reason: <c>KEY</c> is reserved in T-SQL, and this repository has already paid for one T-SQL
/// trap that a seam's shape hid (<see cref="IAlvoSqlDialect.RowLockClause"/>).
/// </para>
/// <para>
/// <b>The record stores a row id, not a response body.</b> A replay re-reads the row through the caller's
/// current <c>get</c> policy, so it can never hand back a representation that policy would not produce today.
/// Caching the body would also make this table grow with payload size and require an eviction rule nobody has
/// designed.
/// </para>
/// <para>
/// <b>No <c>entity</c> column.</b> One was stored and never read, which is a control that does not exist: it
/// made a key unique per scope across every entity while telling the lookup nothing. The fingerprint covers
/// the entity by contract (see <see cref="AlvoIdempotency.Fingerprint"/>), so a matched fingerprint already
/// proves the replay is for the entity the original wrote, and the same key on a different entity is a
/// conflict like any other different request.
/// </para>
/// <para>
/// <b>Its bind-parameter names are not in <see cref="PolicyParameterPrefix"/>.</b> That registry exists
/// because one composed read statement carries fragments from several renderers that never see each other's
/// output; these statements are hand-written, single-fragment, and touch no entity table, so there is no
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
    /// <para>
    /// The name is a validated identifier assembled from <see cref="AlvoOptions.SchemaPrefix"/> (see
    /// <see cref="SystemSchemaInitializer"/>'s constructor guard), never caller-supplied data — and SQL has
    /// no bind-parameter form of an identifier, so interpolation is the only way to place it at all.
    /// </para>
    /// <para>
    /// <b><c>PRIMARY KEY (idempotency_key, scope)</c> is the concurrency control</b>, not a tidy-up: two
    /// requests carrying one key can both find no record and both insert a row, and this constraint is what
    /// makes exactly one of them commit. <c>scope</c> carries the tenant <em>and</em> the acting user rather
    /// than sitting beside the key, because a shared key space lets one client's replay return another's row.
    /// </para>
    /// </remarks>
    internal static string Ddl(string tableName) =>
        $"""
        CREATE TABLE IF NOT EXISTS {tableName} (
            idempotency_key TEXT NOT NULL,
            scope TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            row_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (idempotency_key, scope)
        )
        """;

    /// <summary>
    /// The record stored for one key in one scope, or <see langword="null"/> when the key is unused there.
    /// </summary>
    /// <param name="Fingerprint">The fingerprint of the request the key was first used for.</param>
    /// <param name="RowId">The id of the row that first request created.</param>
    internal readonly record struct IdempotencyRecord(string Fingerprint, Guid RowId);

    /// <summary>
    /// Creates the table if it does not exist yet, on <paramref name="connection"/> and outside any
    /// transaction.
    /// </summary>
    /// <param name="connection">An open connection; opened by the caller, never owned here.</param>
    /// <param name="tableName">The table name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not inside the write transaction, and measured.</b> Run there, this DDL <em>serializes
    /// two concurrent idempotent creates</em>: PostgreSQL will not let two transactions create one table name
    /// at once, so the second blocks on the first until it commits and then finds the record already there.
    /// The result is still correct — but the primary key, which is the actual concurrency control, is never
    /// reached, so <c>Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row</c> passed with
    /// the <c>PRIMARY KEY</c> clause deleted from the DDL above. A guard that cannot fail is not a guard.
    /// </para>
    /// <para>
    /// Outside a transaction it commits on its own, which is also what makes a caller's ensure-once memo
    /// honest: a memo set inside a transaction that later rolls back would claim a table exists that was
    /// rolled back with everything else. Two connections racing the very first create may still collide here
    /// — a duplicate-relation error on PostgreSQL — which is a storage write failure like any other and is
    /// retried by the caller, whose next attempt finds the table in place.
    /// </para>
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

    /// <summary>Reads the record for one key in one scope, inside the caller's transaction.</summary>
    /// <param name="connection">The write transaction's own connection.</param>
    /// <param name="transaction">The in-flight write transaction, so the read cannot see an uncommitted rival.</param>
    /// <param name="tableName">The table name.</param>
    /// <param name="key">The caller's key, bound as a value.</param>
    /// <param name="scope">The key's scope, from <see cref="AlvoIdempotency.IdentityOf"/>.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal static async Task<IdempotencyRecord?> FindAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string key,
        string scope,
        CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT fingerprint, row_id FROM {tableName} WHERE idempotency_key = @key AND scope = @scope";
            RelationalSqlBatch.AddParameter(command, "@key", key);
            RelationalSqlBatch.AddParameter(command, "@scope", scope);

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
    /// <param name="scope">The key's scope, from <see cref="AlvoIdempotency.IdentityOf"/>.</param>
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
        string scope,
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
                INSERT INTO {tableName} (idempotency_key, scope, fingerprint, row_id, created_at)
                VALUES (@key, @scope, @fingerprint, @row_id, @created_at)
                """;
            RelationalSqlBatch.AddParameter(command, "@key", token.Key);
            RelationalSqlBatch.AddParameter(command, "@scope", scope);
            RelationalSqlBatch.AddParameter(command, "@fingerprint", token.Fingerprint);
            RelationalSqlBatch.AddParameter(command, "@row_id", rowId.ToString());
            RelationalSqlBatch.AddParameter(command, "@created_at", StoredInstant.Text(createdAt));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
