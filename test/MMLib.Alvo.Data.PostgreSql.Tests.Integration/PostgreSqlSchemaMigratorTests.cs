using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Runs the full <see cref="SchemaMigratorContractTests"/> suite against a real PostgreSQL
/// server, wired exclusively through the public
/// <see cref="AlvoPostgreSqlBuilderExtensions.UsePostgreSql"/> entry point — the same path a host
/// application would use.
/// </summary>
/// <remarks>
/// The server itself (the Testcontainers container) is shared for the whole class via
/// <see cref="PostgresFixture"/> — starting a container per test would be needlessly slow. Each
/// test instance, however, still needs its own isolated database: the contract suite creates a
/// "vehicles" table from an empty schema in more than one test, and a shared database would make
/// the second test to run fail with "relation already exists". A fresh, cheaply-created
/// <c>CREATE DATABASE</c> per test instance (mirroring the Sqlite suite's fresh-file-per-instance
/// isolation) gives that without paying for a second container.
/// </remarks>
public sealed class PostgreSqlSchemaMigratorTests : SchemaMigratorContractTests, IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _databaseName = $"alvo_test_{Guid.NewGuid():N}";
    private readonly ServiceProvider _services;

    public PostgreSqlSchemaMigratorTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        CreateDatabase(fixture.ConnectionString, _databaseName);

        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(WithDatabase(fixture.ConnectionString, _databaseName));
        _services = builder.Services.BuildServiceProvider();
    }

    protected override ISchemaMigrator CreateMigrator() => _services.GetRequiredService<ISchemaMigrator>();

    protected override Task<SchemaModel> IntrospectAsync() =>
        _services.GetRequiredService<ISchemaIntrospector>().IntrospectAsync();

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
