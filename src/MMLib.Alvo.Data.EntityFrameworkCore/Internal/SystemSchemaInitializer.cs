using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Idempotently creates Alvo's own fixed append-only descriptor-versions table — the framework's
/// bookkeeping table, not something produced by the declarative descriptor-diff engine
/// (<see cref="Migrations.ISchemaMigrator"/>).
/// </summary>
/// <remarks>
/// The DDL is written to be identical on SQLite and PostgreSQL (a single <c>CREATE TABLE IF NOT
/// EXISTS</c> with only ANSI-portable column types), so this class needs no per-engine branching.
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
    }

    /// <summary>Gets the fully-prefixed descriptor-versions table name, e.g. <c>alvo_descriptor_versions</c>.</summary>
    public string TableName { get; }

    /// <summary>
    /// Computes the descriptor-versions table name for a given prefix — the single source of truth
    /// <see cref="EfCoreSchemaIntrospector"/> reuses to exclude Alvo's own bookkeeping table from
    /// what it reports as the user's schema.
    /// </summary>
    public static string DescriptorVersionsTableName(string schemaPrefix) => $"{schemaPrefix}_descriptor_versions";

    /// <summary>
    /// Creates the descriptor-versions table if it does not already exist. Safe to call repeatedly —
    /// a second (or Nth) call is a no-op.
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

        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            // The table name is a validated identifier (see the ctor guard above), not
            // attacker-controlled data, so interpolating it is safe — SQL parameters can only
            // bind values, never identifiers, so this is the only way to parameterize it anyway.
            command.CommandText =
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
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,15}$")]
    private static partial Regex SchemaPrefixPattern();
}
