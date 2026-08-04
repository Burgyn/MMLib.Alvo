using MMLib.Alvo.Tests.Data;

using Npgsql;

using System.Data.Common;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Runs the whole <see cref="OutboxTableFacts"/> suite against a real PostgreSQL server — the engine that
/// refuses <c>AUTOINCREMENT</c>, which is why the ordering key is a UUIDv7 and the DDL needs no per-engine
/// branching at all.
/// </summary>
/// <remarks>
/// The server is shared for the whole class via <see cref="PostgresFixture"/>; the <em>database</em> is fresh
/// per test instance, mirroring <see cref="PostgreSqlDescriptorVersionStoreTests"/>'s isolation, because the
/// inherited facts count rows and one test's insert must not be another's starting state. Nothing is dropped
/// here: the container's own disposal tears down every database created inside it.
/// </remarks>
public sealed class PostgreSqlOutboxTableTests : OutboxTableFacts, IClassFixture<PostgresFixture>
{
    private readonly string _connectionString = string.Empty;

    public PostgreSqlOutboxTableTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // The fixture never started a container (a Windows-container runner cannot run the Linux
            // postgres:16-alpine image), so every inherited fact skips on EnsureEngineAvailable() below
            // before it ever asks for a connection.
            return;
        }

        var databaseName = $"alvo_outbox_{Guid.NewGuid():N}";
        CreateDatabase(fixture.ConnectionString, databaseName);
        _connectionString = WithDatabase(fixture.ConnectionString, databaseName);
    }

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    protected override DbConnection CreateConnection() => new NpgsqlConnection(_connectionString);

    private static void CreateDatabase(string adminConnectionString, string databaseName)
    {
        using var connection = new NpgsqlConnection(adminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        command.ExecuteNonQuery();
    }

    private static string WithDatabase(string connectionString, string databaseName) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
}
