using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Runs the shared <see cref="SchemaSqlSnapshotTests"/> suite against a real SQLite database file,
/// wired exclusively through the public <see cref="AlvoSqliteBuilderExtensions.UseSqlite"/> entry
/// point — the same path a host application would use. See the equivalent
/// <c>PostgreSqlGeneratedSqlSnapshotTests</c> in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>
/// for the same canonical set on PostgreSQL.
/// </summary>
public sealed class SqliteGeneratedSqlSnapshotTests : SchemaSqlSnapshotTests, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-sqlite-sql-snapshots-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public SqliteGeneratedSqlSnapshotTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        _services = builder.Services.BuildServiceProvider();
    }

    protected override string EngineName => "sqlite";

    protected override ISchemaMigrator CreateMigrator() => _services.GetRequiredService<ISchemaMigrator>();

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
