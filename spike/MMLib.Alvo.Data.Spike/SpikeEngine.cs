using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.Conventions;
using System.Data.Common;
using Testcontainers.PostgreSql;

namespace MMLib.Alvo.Data.Spike;

/// <summary>One real engine the spike replays every probe against.</summary>
public abstract class SpikeEngine : IAsyncDisposable
{
    public abstract string Name { get; }

    public abstract Func<ModelBuilder> NewModelBuilder { get; }

    public abstract IFieldSqlRenderer Fields { get; }

    /// <summary>The schema every spike table lives in, or <see langword="null"/> for the engine's default.</summary>
    public virtual string? Schema => null;

    public abstract DbConnection CreateConnection();

    public abstract void UseProvider(DbContextOptionsBuilder options, DbConnection connection);

    public abstract DbParameter CreateParameter(string name, object? value);

    /// <summary>The engine's column type for a field — the spike creates its tables by hand, not through the migrator.</summary>
    public abstract string ColumnType(FieldType type);

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SqliteSpikeEngine : SpikeEngine
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"alvo-spike-{Guid.NewGuid():N}.db");

    public override string Name => "SQLite";

    public override Func<ModelBuilder> NewModelBuilder => static () => new ModelBuilder(SqliteConventionSetBuilder.Build());

    public override IFieldSqlRenderer Fields { get; } = new SqliteFieldSqlRenderer();

    public override DbConnection CreateConnection() =>
        new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());

    public override void UseProvider(DbContextOptionsBuilder options, DbConnection connection) =>
        options.UseSqlite((SqliteConnection)connection);

    public override DbParameter CreateParameter(string name, object? value) =>
        new SqliteParameter(name, value ?? DBNull.Value);

    public override string ColumnType(FieldType type) => type switch
    {
        FieldType.Uuid or FieldType.Ref => "TEXT",
        FieldType.String or FieldType.Text or FieldType.Json or FieldType.Enum => "TEXT",
        FieldType.Integer => "INTEGER",
        FieldType.Decimal => "TEXT",
        FieldType.Boolean => "INTEGER",
        FieldType.Date or FieldType.DateTime => "TEXT",
        _ => throw new NotSupportedException(type.ToString()),
    };

    public override ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Spike cleanup; a leftover temp file is not a finding.
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class PostgresSpikeEngine : SpikeEngine
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public override string Name => "PostgreSQL";

    public override Func<ModelBuilder> NewModelBuilder => static () => new ModelBuilder(NpgsqlConventionSetBuilder.Build());

    public override IFieldSqlRenderer Fields { get; } = new PostgresFieldSqlRenderer();

    // Deliberately NOT "public": question 8 asks whether a schema-qualified name works on both engines.
    public override string? Schema => "alvo_spike";

    public override DbConnection CreateConnection() => new NpgsqlConnection(_container.GetConnectionString());

    public override void UseProvider(DbContextOptionsBuilder options, DbConnection connection) =>
        options.UseNpgsql((NpgsqlConnection)connection);

    public override DbParameter CreateParameter(string name, object? value) =>
        new NpgsqlParameter(name, value ?? DBNull.Value);

    public override string ColumnType(FieldType type) => type switch
    {
        FieldType.Uuid or FieldType.Ref => "uuid",
        FieldType.String or FieldType.Text or FieldType.Enum => "text",
        FieldType.Json => "jsonb",
        FieldType.Integer => "bigint",
        FieldType.Decimal => "numeric(18,2)",
        FieldType.Boolean => "boolean",
        FieldType.Date => "date",
        FieldType.DateTime => "timestamptz",
        _ => throw new NotSupportedException(type.ToString()),
    };

    public override async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{Schema}\";";
        await command.ExecuteNonQueryAsync();
    }

    public override ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>The SQLite half of <see cref="IFieldSqlRenderer"/> — what PR2's SQLite driver would ship.</summary>
public sealed class SqliteFieldSqlRenderer : IFieldSqlRenderer
{
    public string TrueLiteral => "1";

    public string FalseLiteral => "0";

    public string RenderField(EntitySchema entity, string fieldName) => Quote(fieldName);

    public string RenderParameter(string parameterName) => "@" + parameterName;

    public string RenderCaseInsensitiveLike(string left, string right) => $"UPPER({left}) LIKE UPPER({right})";

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>The PostgreSQL half of <see cref="IFieldSqlRenderer"/> — what PR2's PostgreSQL driver would ship.</summary>
public sealed class PostgresFieldSqlRenderer : IFieldSqlRenderer
{
    public string TrueLiteral => "TRUE";

    public string FalseLiteral => "FALSE";

    public string RenderField(EntitySchema entity, string fieldName) => Quote(fieldName);

    public string RenderParameter(string parameterName) => "@" + parameterName;

    public string RenderCaseInsensitiveLike(string left, string right) => $"{left} ILIKE {right}";

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
