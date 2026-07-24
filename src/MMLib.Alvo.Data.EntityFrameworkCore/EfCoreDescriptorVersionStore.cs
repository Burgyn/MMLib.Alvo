using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
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
/// connection. The write path — lazy table creation, current-revision read, row insert, and the
/// optimistic-lock conflict translation — is delegated to the shared <see cref="VersionRowWriter"/>,
/// the same helper the atomic <see cref="EfCoreRuntimeSchemaWriter"/> uses, so both speak identical
/// SQL. For <see cref="AppendAsync"/>, the read-current-then-insert conditional write happens inside
/// a single transaction on one connection, so the check and the insert are atomic.
/// </remarks>
internal sealed class EfCoreDescriptorVersionStore : IDescriptorVersionStore, IAppliedSchemaStore, IDisposable
{
    private const string SelectColumns = "descriptor_json, schema_json, revision, author, reason, rolled_back_from, created_at";

    private readonly RelationalConnectionFactory _connections;
    private readonly VersionRowWriter _rows;
    private readonly string _schemaPrefix;

    public EfCoreDescriptorVersionStore(RelationalConnectionFactory connections, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _rows = new VersionRowWriter(connections, options);
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
            await _rows.EnsureReadyAsync(connection, ct).ConfigureAwait(false);
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
            await _rows.EnsureReadyAsync(connection, ct).ConfigureAwait(false);
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
            await _rows.EnsureReadyAsync(connection, ct).ConfigureAwait(false);
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
            await _rows.EnsureReadyAsync(connection, ct).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var current = await _rows.ReadCurrentRevisionAsync(connection, transaction, project, ct).ConfigureAwait(false);
                if (current != expectedRevision)
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    throw new DescriptorConcurrencyException(project, expectedRevision, current);
                }

                var appended = candidate with { Revision = expectedRevision + 1 };
                try
                {
                    await _rows.InsertAsync(connection, transaction, project, appended, ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }
                catch (DbException)
                {
                    await _rows.ThrowIfConcurrencyConflictAsync(transaction, project, expectedRevision, ct).ConfigureAwait(false);
                    throw;
                }

                return appended;
            }
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

    private async Task<DescriptorVersion?> SelectOneAsync(DbConnection connection, string project, int? revision, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            // TODO: ORDER BY ... LIMIT 1 is SQLite/PostgreSQL syntax. Azure SQL (a named §0
            // engine-agnostic target) has no LIMIT — when that provider lands, this needs a
            // portable fetch instead (e.g. a MAX(revision) subquery join, or OFFSET ... FETCH NEXT).
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
    /// Disposes the shared <see cref="VersionRowWriter"/> (its ensure-once gate). The per-call
    /// <see cref="DbConnection"/>s created through <see cref="RelationalConnectionFactory"/> are not
    /// owned by this type and are already disposed at the end of each call.
    /// </summary>
    public void Dispose() => _rows.Dispose();
}
