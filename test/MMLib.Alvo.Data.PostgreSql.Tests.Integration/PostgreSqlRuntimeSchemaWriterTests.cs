using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Runs the full <see cref="RuntimeSchemaWriterContractTests"/> suite against a real PostgreSQL
/// server, wired exclusively through the public
/// <see cref="AlvoPostgreSqlBuilderExtensions.UsePostgreSql"/> entry point. This is where the
/// writer's atomic apply-plus-append gate is exercised on Postgres's transactional DDL — the
/// engine-parity leg to the SQLite suite.
/// </summary>
/// <remarks>
/// The Testcontainers server is shared for the class via <see cref="PostgresFixture"/>; each test
/// instance gets its own freshly-created database (the contract suite reuses project name "p"), so
/// one instance's history cannot bleed into another's — mirroring
/// <see cref="PostgreSqlDescriptorVersionStoreTests"/>.
/// </remarks>
public sealed class PostgreSqlRuntimeSchemaWriterTests : RuntimeSchemaWriterContractTests, IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _databaseName = $"alvo_test_{Guid.NewGuid():N}";
    private readonly ServiceProvider _services;

    public PostgreSqlRuntimeSchemaWriterTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // The fixture never started a container (Windows-container runners can't run the
            // Linux postgres:16-alpine image), so every test below calls EnsureEngineAvailable()
            // as its first statement and skips before CreateWriter() ever touches _services.
            _services = new ServiceCollection().BuildServiceProvider();
            return;
        }

        CreateDatabase(fixture.ConnectionString, _databaseName);
        var connectionString = WithDatabase(fixture.ConnectionString, _databaseName);

        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(connectionString);
        _services = builder.Services.BuildServiceProvider();
    }

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    protected override IRuntimeSchemaWriter CreateWriter() => _services.GetRequiredService<IRuntimeSchemaWriter>();

    public void Dispose()
    {
        // The container's disposal (PostgresFixture.DisposeAsync) tears down every database
        // created inside it, including this one — nothing to drop here explicitly.
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

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

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
