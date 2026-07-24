using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// <see cref="IAppliedSchemaStore"/> backed by a single row-per-project table, reached through a
/// provider-supplied <see cref="DbConnection"/> and engine-agnostic SQL (identical on SQLite and
/// PostgreSQL) — no EF model, no concrete ADO.NET provider reference.
/// </summary>
/// <remarks>
/// The applied-schema table is created lazily, on first use, via <see cref="SystemSchemaInitializer"/>
/// (ensure-once, guarded so concurrent callers don't race the <c>CREATE TABLE</c>). The upsert uses
/// <c>INSERT ... ON CONFLICT(project) DO UPDATE</c>, which SQLite and PostgreSQL both support with
/// identical syntax.
/// </remarks>
internal sealed class AppliedSchemaStore : IAppliedSchemaStore, IDisposable
{
    private readonly DbConnection _connection;
    private readonly SystemSchemaInitializer _initializer;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    public AppliedSchemaStore(DbConnection connection, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        _connection = connection;
        _initializer = new SystemSchemaInitializer(connection, options.SchemaPrefix);
    }

    /// <inheritdoc/>
    public async Task<AppliedSchema?> GetCurrentAsync(string project, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        await EnsureReadyAsync(ct).ConfigureAwait(false);

        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText =
                $"SELECT descriptor_json, schema_json, revision, updated_at FROM {_initializer.TableName} WHERE project = @project";
            AddParameter(command, "@project", project);

            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(ct).ConfigureAwait(false)
                    ? ReadAppliedSchema(reader, project)
                    : null;
            }
        }
    }

    private static AppliedSchema ReadAppliedSchema(DbDataReader reader, string project)
    {
        var descriptorJson = reader.GetString(0);
        var schemaJson = reader.GetString(1);
        var revision = reader.GetInt32(2);
        var updatedAt = DateTimeOffset.Parse(
            reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var schema = JsonSerializer.Deserialize(schemaJson, AppliedSchemaJsonContext.Default.SchemaModel)
            ?? throw new InvalidOperationException($"Applied schema for project '{project}' deserialized to null.");

        return new AppliedSchema(schema, descriptorJson, revision, updatedAt);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(string project, AppliedSchema snapshot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(snapshot);

        await EnsureReadyAsync(ct).ConfigureAwait(false);

        var command = CreateUpsertCommand(project, snapshot);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    // Upsert: identical syntax on SQLite (3.24+) and PostgreSQL (9.5+). All values are bound as
    // parameters — no string concatenation of data, only of the (validated, non-attacker-controlled)
    // table name.
    private DbCommand CreateUpsertCommand(string project, AppliedSchema snapshot)
    {
        var schemaJson = JsonSerializer.Serialize(snapshot.Schema, AppliedSchemaJsonContext.Default.SchemaModel);

        var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO {_initializer.TableName} (project, descriptor_json, schema_json, revision, updated_at)
            VALUES (@project, @descriptor_json, @schema_json, @revision, @updated_at)
            ON CONFLICT(project) DO UPDATE SET
                descriptor_json = excluded.descriptor_json,
                schema_json = excluded.schema_json,
                revision = excluded.revision,
                updated_at = excluded.updated_at
            """;
        AddParameter(command, "@project", project);
        AddParameter(command, "@descriptor_json", snapshot.DescriptorJson);
        AddParameter(command, "@schema_json", schemaJson);
        AddParameter(command, "@revision", snapshot.Revision);
        AddParameter(command, "@updated_at", snapshot.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

        return command;
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ensured)
        {
            return;
        }

        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured)
            {
                return;
            }

            await _initializer.EnsureAsync(ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Disposes the internal ensure-once gate. The provider-supplied <see cref="DbConnection"/>
    /// is not owned by this type and is left untouched, matching <c>EfCoreSchemaMigrator</c>.
    /// </summary>
    public void Dispose() => _ensureGate.Dispose();
}
