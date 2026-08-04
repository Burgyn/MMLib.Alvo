using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Idempotently creates Alvo's own fixed bookkeeping tables — the append-only descriptor-versions table
/// and the idempotency-record table — neither of which is produced by the declarative descriptor-diff
/// engine (<see cref="Migrations.ISchemaMigrator"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every DDL statement here is written to be identical on SQLite and PostgreSQL (a single <c>CREATE TABLE
/// IF NOT EXISTS</c> with only ANSI-portable column types), so this class needs no per-engine branching.
/// </para>
/// <para>
/// The idempotency table's own DDL lives beside its name in <see cref="IdempotencyTable"/> rather than
/// inline here, because the write path also has to create it on demand: nothing calls this initializer on
/// the data path (only a descriptor <em>apply</em> reaches it), so a host whose schema never came through
/// the mapper would otherwise have the table missing exactly when a create carrying a token needs it. Two
/// creators, one DDL string.
/// </para>
/// </remarks>
internal sealed partial class SystemSchemaInitializer
{
    private readonly DbConnection _connection;

    public SystemSchemaInitializer(DbConnection connection, string schemaPrefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPrefix);

        if (!SchemaPrefixPattern().IsMatch(schemaPrefix))
        {
            throw new ArgumentException(
                $"Schema prefix '{schemaPrefix}' must be lower snake_case, 1-16 chars (matching AlvoOptions.SchemaPrefix's validation).",
                nameof(schemaPrefix));
        }

        _connection = connection;
        TableName = DescriptorVersionsTableName(schemaPrefix);
        _idempotencyTableName = IdempotencyTable.NameFor(schemaPrefix);
    }

    private readonly string _idempotencyTableName;

    /// <summary>Gets the fully-prefixed descriptor-versions table name, e.g. <c>alvo_descriptor_versions</c>.</summary>
    public string TableName { get; }

    /// <summary>
    /// Computes the descriptor-versions table name for a given prefix — the single source of truth
    /// <see cref="EfCoreSchemaIntrospector"/> reuses to exclude Alvo's own bookkeeping table from
    /// what it reports as the user's schema.
    /// </summary>
    public static string DescriptorVersionsTableName(string schemaPrefix) => $"{schemaPrefix}_descriptor_versions";

    /// <summary>
    /// Every table this initializer owns, for a given prefix — the set
    /// <see cref="EfCoreSchemaIntrospector"/> excludes from what it reports as the user's schema. One
    /// member rather than a name per caller: an introspector that knows about one framework table and not
    /// the next one added would plan a <c>DROP</c> for it on the following re-apply, silently, and the
    /// symptom would be a lost idempotency history rather than an error.
    /// </summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    public static IReadOnlyList<string> FrameworkTableNames(string schemaPrefix) =>
        [DescriptorVersionsTableName(schemaPrefix), IdempotencyTable.NameFor(schemaPrefix)];

    /// <summary>
    /// Creates the framework's bookkeeping tables if they do not already exist. Safe to call repeatedly —
    /// a second (or Nth) call is a no-op — and safe to call from several processes at once.
    /// </summary>
    // Deferred: Postgres schema cohabitation (spec §2.13) — embedded mode living inside a host's
    // own Postgres schema — is intentionally out of scope for this PR and tracked as follow-up
    // work. PR-A deliberately uses a plain, cross-engine table-name prefix instead, identically on
    // both SQLite and PostgreSQL, rather than a real Postgres DB schema.
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(ct).ConfigureAwait(false);
        }

        // The table names are validated identifiers (see the ctor guard above), not attacker-controlled
        // data, so interpolating them is safe — SQL parameters can only bind values, never identifiers,
        // so this is the only way to parameterize them anyway.
        await CreateIfMissingAsync(TableName, DescriptorVersionsDdl, ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _idempotencyTableName, IdempotencyTable.Ddl(_idempotencyTableName), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one <c>CREATE TABLE IF NOT EXISTS</c>, treating "another connection created it a moment ago" as
    /// the success <c>IF NOT EXISTS</c> was asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>CREATE TABLE IF NOT EXISTS</c> is not concurrency-safe on PostgreSQL, and this was measured, not
    /// assumed.</b> PostgreSQL's own documentation says so: the existence check and the catalog insert are not
    /// atomic, so two sessions creating one table at the same instant leave the loser with
    /// <c>23505 duplicate key value violates unique constraint "pg_type_typname_nsp_index"</c> rather than a
    /// quiet no-op. Three replicas cold-starting against one empty database do exactly that, and before this
    /// they failed their <em>first</em> database call — stage 1 — never reaching the applied-snapshot race the
    /// boot's own convergence handles.
    /// </para>
    /// <para>
    /// The recovery reads the outcome instead of the error: no <c>SQLSTATE</c> and no
    /// <c>SqliteErrorCode</c> is inspected, because a driver that decoded engine error numbers here would need
    /// a new branch for every engine Alvo adds. If the table is there afterwards, the intent was met by
    /// whoever created it; if it is not, the failure was something else and is rethrown unchanged. That is the
    /// same "re-read rather than classify" discipline <see cref="VersionRowWriter"/> uses to tell a lost race
    /// from a genuine write failure.
    /// </para>
    /// <para>
    /// The DDL runs outside any transaction, so a failed statement leaves the connection usable and the
    /// existence probe below can run on it directly.
    /// </para>
    /// </remarks>
    /// <param name="tableName">The table the DDL creates.</param>
    /// <param name="ddl">The <c>CREATE TABLE IF NOT EXISTS</c> statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    private async Task CreateIfMissingAsync(string tableName, string ddl, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(ddl, ct).ConfigureAwait(false);
        }
        catch (DbException)
        {
            if (!await ExistsAsync(tableName, ct).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>Whether <paramref name="tableName"/> can be selected from, i.e. whether it exists.</summary>
    /// <remarks>
    /// A zero-row select rather than a catalog query: SQLite has <c>sqlite_master</c>, PostgreSQL has
    /// <c>information_schema</c>, and asking the table itself is the one question every engine answers the
    /// same way. Nothing is returned, so the shape of the table does not matter either.
    /// </remarks>
    /// <param name="tableName">The table to look for.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    private async Task<bool> ExistsAsync(string tableName, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync($"SELECT 1 FROM {tableName} WHERE 1 = 0", ct).ConfigureAwait(false);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private string DescriptorVersionsDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {TableName} (
            project TEXT NOT NULL,
            revision INTEGER NOT NULL,
            descriptor_json TEXT NOT NULL,
            schema_json TEXT NOT NULL,
            author TEXT NULL,
            reason TEXT NULL,
            rolled_back_from INTEGER NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (project, revision)
        )
        """;

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,15}$")]
    private static partial Regex SchemaPrefixPattern();
}
