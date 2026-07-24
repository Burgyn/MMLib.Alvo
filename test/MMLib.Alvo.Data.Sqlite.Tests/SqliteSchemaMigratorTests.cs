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

        // Best-effort: the migrator/introspector dispose their (pooling-disabled) connections
        // above, which is what actually releases the OS file handle. This is still a temp file
        // either way, so a stray lock (e.g. an antivirus scan on Windows) should not fail the test
        // — the OS reclaims temp files regardless.
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
