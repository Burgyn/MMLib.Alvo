using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Runs the full <see cref="SchemaMigratorContractTests"/> suite against a real SQLite database
/// file, wired exclusively through the public <see cref="AlvoSqliteBuilderExtensions.UseSqlite"/>
/// entry point — the same path a host application would use.
/// </summary>
public sealed class SqliteSchemaMigratorTests : SchemaMigratorContractTests, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-sqlite-tests-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public SqliteSchemaMigratorTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        _services = builder.Services.BuildServiceProvider();
    }

    protected override ISchemaMigrator CreateMigrator() => _services.GetRequiredService<ISchemaMigrator>();

    protected override Task<SchemaModel> IntrospectAsync() =>
        _services.GetRequiredService<ISchemaIntrospector>().IntrospectAsync();

    public void Dispose()
    {
        _services.Dispose();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
