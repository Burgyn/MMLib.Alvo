using MMLib.Alvo.Migrations;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The one implementation of the descriptor-versions <em>write path</em> — lazy table creation,
/// current-revision read, parameterized row insert, and the engine-agnostic optimistic-lock
/// conflict translation — shared by <see cref="EfCoreDescriptorVersionStore"/> (non-atomic append)
/// and <see cref="EfCoreRuntimeSchemaWriter"/> (atomic apply-plus-append) so both speak identical
/// SQL and translate a lost race the same way.
/// </summary>
/// <remarks>
/// This type owns only the write-side primitives; reading history (GetCurrent/Get/List) stays in
/// the store. The primitives are composable on purpose: the store commits right after the insert,
/// while the runtime writer runs the plan's DDL between the insert and the commit — both inside one
/// transaction — so the conflict translation cannot be baked into a single insert-and-commit call.
/// </remarks>
internal sealed class VersionRowWriter : IDisposable
{
    private readonly RelationalConnectionFactory _connections;
    private readonly string _schemaPrefix;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private volatile bool _ensured;

    public VersionRowWriter(RelationalConnectionFactory connections, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _schemaPrefix = options.SchemaPrefix;
    }

    private string TableName => SystemSchemaInitializer.DescriptorVersionsTableName(_schemaPrefix);

    /// <summary>Opens <paramref name="connection"/> and creates the descriptor-versions table once (ensure-once, race-guarded).</summary>
    public async Task EnsureReadyAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        if (_ensured)
        {
            return;
        }

        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_ensured)
            {
                await new SystemSchemaInitializer(connection, _schemaPrefix).EnsureAsync(ct).ConfigureAwait(false);
                _ensured = true;
            }
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    // Reads the current max revision. Pass the in-flight transaction when this is the atomic
    // check-then-insert pre-check (so no other transaction can slip a conflicting append between the
    // read and the write); pass null for the post-conflict re-read on a fresh connection, where there
    // is no ambient transaction to join.
    public async Task<int> ReadCurrentRevisionAsync(DbConnection connection, DbTransaction? transaction, string project, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT COALESCE(MAX(revision), 0) FROM {TableName} WHERE project = @project";
            RelationalSqlBatch.AddParameter(command, "@project", project);

            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
    }

    // All values are bound as parameters — no string concatenation of data, only of the
    // (validated, non-attacker-controlled) table name.
    public async Task InsertAsync(DbConnection connection, DbTransaction transaction, string project, DescriptorVersion version, CancellationToken ct)
    {
        var schemaJson = JsonSerializer.Serialize(version.Schema, AppliedSchemaJsonContext.Default.SchemaModel);

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText =
                $"""
                INSERT INTO {TableName} (project, revision, descriptor_json, schema_json, author, reason, rolled_back_from, created_at)
                VALUES (@project, @revision, @descriptor_json, @schema_json, @author, @reason, @rolled_back_from, @created_at)
                """;
            RelationalSqlBatch.AddParameter(command, "@project", project);
            RelationalSqlBatch.AddParameter(command, "@revision", version.Revision);
            RelationalSqlBatch.AddParameter(command, "@descriptor_json", version.DescriptorJson);
            RelationalSqlBatch.AddParameter(command, "@schema_json", schemaJson);
            RelationalSqlBatch.AddParameter(command, "@author", (object?)version.Author ?? DBNull.Value);
            RelationalSqlBatch.AddParameter(command, "@reason", (object?)version.Reason ?? DBNull.Value);
            RelationalSqlBatch.AddParameter(command, "@rolled_back_from", (object?)version.RolledBackFrom ?? DBNull.Value);
            RelationalSqlBatch.AddParameter(command, "@created_at", StoredInstant.Text(version.CreatedAt));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    // Engine-agnostic conflict translation, invoked from the caller's catch(DbException) after an
    // insert/commit fails. The pre-check read in the caller gives a clean, fast rejection for the
    // SEQUENTIAL conflict case, but it is not sufficient under GENUINE concurrency: two independent
    // connections can both read the same MAX(revision) before either commits (SQLite's deferred
    // transactions and PostgreSQL's default READ COMMITTED both allow it), so both can pass the
    // pre-check and race the INSERT. The composite PRIMARY KEY (project, revision) is what actually
    // protects integrity in that race; this method turns the loser's raw constraint-violation
    // DbException into the contractually-promised DescriptorConcurrencyException.
    //
    // It never inspects a provider-specific error code (SqliteErrorCode / SqlState). Instead it rolls
    // back and re-reads the current max revision on a fresh connection:
    //  - if the fresh max no longer equals expectedRevision, someone else's append won the race →
    //    throw DescriptorConcurrencyException with the now-current revision.
    //  - if the fresh max is STILL expectedRevision, the insert failed for some OTHER reason and
    //    nobody advanced the history → return normally so the caller rethrows the original
    //    DbException, and a genuine failure is never masked as a (spurious) concurrency conflict.
    public async Task ThrowIfConcurrencyConflictAsync(DbTransaction transaction, string project, int expectedRevision, CancellationToken ct)
    {
        await RollbackQuietlyAsync(transaction, ct).ConfigureAwait(false);
        var actual = await ReadCurrentRevisionOnFreshConnectionAsync(project, ct).ConfigureAwait(false);
        if (actual != expectedRevision)
        {
            throw new DescriptorConcurrencyException(project, expectedRevision, actual);
        }
    }

    /// <summary>Disposes the ensure-once gate. Per-call connections are not owned here.</summary>
    public void Dispose() => _ensureGate.Dispose();

    // Best-effort: the connection behind `transaction` may already be broken by whatever caused the
    // insert/commit to fail, in which case the rollback itself can throw. That failure is not the
    // signal we care about here — the fresh re-read right after this is.
    private static async Task RollbackQuietlyAsync(DbTransaction transaction, CancellationToken ct)
    {
        try
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
        catch (DbException)
        {
        }
    }

    private async Task<int> ReadCurrentRevisionOnFreshConnectionAsync(string project, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await EnsureReadyAsync(connection, ct).ConfigureAwait(false);
            return await ReadCurrentRevisionAsync(connection, transaction: null, project, ct).ConfigureAwait(false);
        }
    }
}
