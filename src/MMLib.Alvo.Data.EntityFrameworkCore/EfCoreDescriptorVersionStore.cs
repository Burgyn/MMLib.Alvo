using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Append-only <see cref="IDescriptorVersionStore"/> (and back-compatible <see cref="IAppliedSchemaStore"/>)
/// over a single <c>{prefix}_descriptor_versions</c> table, reached through per-call connections and
/// engine-agnostic SQL (identical on SQLite and PostgreSQL).
/// </summary>
/// <remarks>
/// Every call opens a fresh <see cref="DbConnection"/> from the injected
/// <see cref="RelationalConnectionFactory"/> so concurrent callers never race on a shared
/// connection. The table is created lazily on first use (ensure-once, guarded so concurrent
/// callers don't race the <c>CREATE TABLE</c>) and, for <see cref="AppendAsync"/>, the
/// read-current-then-insert conditional write happens inside a single transaction on one
/// connection, so the check and the insert are atomic.
/// </remarks>
internal sealed class EfCoreDescriptorVersionStore : IDescriptorVersionStore, IAppliedSchemaStore, IDisposable
{
    private const string SelectColumns = "descriptor_json, schema_json, revision, author, reason, rolled_back_from, created_at";

    private readonly RelationalConnectionFactory _connections;
    private readonly string _schemaPrefix;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private volatile bool _ensured;

    public EfCoreDescriptorVersionStore(RelationalConnectionFactory connections, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _schemaPrefix = options.SchemaPrefix;
    }

    private string TableName => SystemSchemaInitializer.DescriptorVersionsTableName(_schemaPrefix);

    /// <inheritdoc cref="IDescriptorVersionStore.GetCurrentAsync"/>
    public async Task<DescriptorVersion?> GetCurrentAsync(string project, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await EnsureReadyAsync(connection, ct).ConfigureAwait(false);
            return await SelectOneAsync(connection, project, revision: null, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<DescriptorVersion?> GetAsync(string project, int revision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await EnsureReadyAsync(connection, ct).ConfigureAwait(false);
            return await SelectOneAsync(connection, project, revision, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DescriptorVersion>> ListAsync(string project, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await EnsureReadyAsync(connection, ct).ConfigureAwait(false);
            return await SelectHistoryAsync(connection, project, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<DescriptorVersion> AppendAsync(string project, DescriptorVersion candidate, int expectedRevision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(candidate);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await EnsureReadyAsync(connection, ct).ConfigureAwait(false);
            var transaction = await BeginAsync(connection, ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var current = await ReadCurrentRevisionAsync(connection, transaction, project, ct).ConfigureAwait(false);
                if (current != expectedRevision)
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    throw new DescriptorConcurrencyException(project, expectedRevision, current);
                }

                var appended = candidate with { Revision = expectedRevision + 1 };
                await CommitInsertOrThrowConflictAsync(connection, transaction, project, appended, expectedRevision, ct).ConfigureAwait(false);
                return appended;
            }
        }
    }

    // The pre-check in AppendAsync (read-max, compare, then insert on the same transaction) gives a
    // clean, fast rejection for the SEQUENTIAL conflict case, but it is not sufficient under GENUINE
    // concurrency: two independent connections can both read the same MAX(revision) before either
    // commits — both SQLite's deferred transactions and PostgreSQL's default READ COMMITTED allow
    // two readers to see the same snapshot — so both can pass the pre-check and race the INSERT. The
    // composite PRIMARY KEY (project, revision) is what actually protects data integrity in that
    // race; this method's job is to turn the loser's raw constraint-violation DbException into the
    // contractually-promised DescriptorConcurrencyException instead of letting it leak to the caller.
    //
    // Engine-agnostic by construction: it never inspects a provider-specific error code
    // (SqliteErrorCode / SqlState). Instead, after ANY DbException from the insert/commit, it rolls
    // back and re-reads the current max revision on a fresh connection:
    //  - if the fresh max no longer equals expectedRevision, someone else's append won the race →
    //    throw DescriptorConcurrencyException with the now-current revision.
    //  - if the fresh max is STILL expectedRevision, our insert didn't land for some OTHER reason
    //    (e.g. a dropped connection) and nobody else advanced the history → rethrow the original
    //    DbException so a genuine failure is never masked as a (spurious) concurrency conflict.
    private async Task CommitInsertOrThrowConflictAsync(
        DbConnection connection, DbTransaction transaction, string project, DescriptorVersion appended, int expectedRevision, CancellationToken ct)
    {
        try
        {
            await InsertAsync(connection, transaction, project, appended, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbException)
        {
            await RollbackQuietlyAsync(transaction, ct).ConfigureAwait(false);
            var actual = await ReadCurrentRevisionOnFreshConnectionAsync(project, ct).ConfigureAwait(false);
            if (actual != expectedRevision)
            {
                throw new DescriptorConcurrencyException(project, expectedRevision, actual);
            }

            throw;
        }
    }

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

    /// <inheritdoc cref="IAppliedSchemaStore.GetCurrentAsync"/>
    async Task<AppliedSchema?> IAppliedSchemaStore.GetCurrentAsync(string project, CancellationToken ct)
    {
        var version = await GetCurrentAsync(project, ct).ConfigureAwait(false);
        return version is null ? null : ToAppliedSchema(version);
    }

    /// <inheritdoc/>
    async Task IAppliedSchemaStore.SaveAsync(string project, AppliedSchema snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var candidate = new DescriptorVersion(snapshot.Schema, snapshot.DescriptorJson, Revision: 0, snapshot.UpdatedAt);
        await AppendAsync(project, candidate, snapshot.Revision - 1, ct).ConfigureAwait(false);
    }

    // Intentionally narrows: AppliedSchema has no Author/Reason/RolledBackFrom, so this mapping
    // drops them by design — IAppliedSchemaStore is the back-compat, revision-only view onto the
    // richer DescriptorVersion history, not a bug or missing feature.
    private static AppliedSchema ToAppliedSchema(DescriptorVersion version) =>
        new(version.Schema, version.DescriptorJson, version.Revision, version.CreatedAt);

    private async Task EnsureReadyAsync(DbConnection connection, CancellationToken ct)
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

    private static ValueTask<DbTransaction> BeginAsync(DbConnection connection, CancellationToken ct) =>
        connection.BeginTransactionAsync(ct);

    // Reads the current max revision. Pass the in-flight transaction when this is the atomic
    // check-then-insert pre-check (so no other transaction can slip a conflicting append between the
    // read and the write); pass null for the post-conflict re-read on a fresh connection, where there
    // is no ambient transaction to join.
    private async Task<int> ReadCurrentRevisionAsync(DbConnection connection, DbTransaction? transaction, string project, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT COALESCE(MAX(revision), 0) FROM {TableName} WHERE project = @project";
            AddParameter(command, "@project", project);

            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
    }

    // All values are bound as parameters — no string concatenation of data, only of the
    // (validated, non-attacker-controlled) table name.
    private async Task InsertAsync(DbConnection connection, DbTransaction transaction, string project, DescriptorVersion version, CancellationToken ct)
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
            AddParameter(command, "@project", project);
            AddParameter(command, "@revision", version.Revision);
            AddParameter(command, "@descriptor_json", version.DescriptorJson);
            AddParameter(command, "@schema_json", schemaJson);
            AddParameter(command, "@author", (object?)version.Author ?? DBNull.Value);
            AddParameter(command, "@reason", (object?)version.Reason ?? DBNull.Value);
            AddParameter(command, "@rolled_back_from", (object?)version.RolledBackFrom ?? DBNull.Value);
            AddParameter(command, "@created_at", version.CreatedAt.ToString("O", CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<DescriptorVersion?> SelectOneAsync(DbConnection connection, string project, int? revision, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = revision is null
                ? $"SELECT {SelectColumns} FROM {TableName} WHERE project = @project ORDER BY revision DESC LIMIT 1"
                : $"SELECT {SelectColumns} FROM {TableName} WHERE project = @project AND revision = @revision";
            AddParameter(command, "@project", project);
            if (revision is not null)
            {
                AddParameter(command, "@revision", revision.Value);
            }

            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadVersion(reader) : null;
            }
        }
    }

    private async Task<IReadOnlyList<DescriptorVersion>> SelectHistoryAsync(DbConnection connection, string project, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE project = @project ORDER BY revision ASC";
            AddParameter(command, "@project", project);

            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var history = new List<DescriptorVersion>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    history.Add(ReadVersion(reader));
                }

                return history;
            }
        }
    }

    private static DescriptorVersion ReadVersion(DbDataReader reader)
    {
        var descriptorJson = reader.GetString(0);
        var schemaJson = reader.GetString(1);
        var revision = reader.GetInt32(2);
        var author = reader.IsDBNull(3) ? null : reader.GetString(3);
        var reason = reader.IsDBNull(4) ? null : reader.GetString(4);
        var rolledBackFrom = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        var createdAt = DateTimeOffset.Parse(
            reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var schema = JsonSerializer.Deserialize(schemaJson, AppliedSchemaJsonContext.Default.SchemaModel)
            ?? throw new InvalidOperationException("Descriptor version deserialized to a null schema.");

        return new DescriptorVersion(schema, descriptorJson, revision, createdAt, author, reason, rolledBackFrom);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Disposes the internal ensure-once gate. The per-call <see cref="DbConnection"/>s created
    /// through <see cref="RelationalConnectionFactory"/> are not owned by this type and are
    /// already disposed at the end of each call.
    /// </summary>
    public void Dispose() => _ensureGate.Dispose();
}
