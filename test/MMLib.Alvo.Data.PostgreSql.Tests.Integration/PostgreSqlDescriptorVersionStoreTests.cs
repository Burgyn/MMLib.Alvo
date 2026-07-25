using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Runs the full <see cref="DescriptorVersionStoreContractTests"/> suite against a real PostgreSQL
/// server, wired exclusively through the public
/// <see cref="AlvoPostgreSqlBuilderExtensions.UsePostgreSql"/> entry point — the same path a host
/// application would use.
/// </summary>
/// <remarks>
/// The server itself (the Testcontainers container) is shared for the whole class via
/// <see cref="PostgresFixture"/> — starting a container per test would be needlessly slow. Each
/// test instance, however, still needs its own isolated database (mirroring
/// <see cref="PostgreSqlSchemaMigratorTests"/>'s fresh-database-per-instance isolation), since the
/// contract suite reuses project name "p" across tests and a shared database would let one test's
/// history bleed into another's.
/// </remarks>
public sealed class PostgreSqlDescriptorVersionStoreTests : DescriptorVersionStoreContractTests, IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _databaseName = $"alvo_test_{Guid.NewGuid():N}";
    private readonly ServiceProvider _services;

    public PostgreSqlDescriptorVersionStoreTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // The fixture never started a container (Windows-container runners can't run the
            // Linux postgres:16-alpine image), so every test below calls EnsureEngineAvailable()
            // as its first statement and skips before CreateStore() ever touches _services.
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

    protected override IDescriptorVersionStore CreateStore() => _services.GetRequiredService<IDescriptorVersionStore>();

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
