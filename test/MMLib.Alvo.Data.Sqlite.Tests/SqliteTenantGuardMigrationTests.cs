using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Tests.Ef;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>The tenant guard (#161) on SQLite: <c>onDelete: setNull</c> is kept as a single-column key beside the guard.</summary>
public sealed class SqliteTenantGuardMigrationTests : TenantGuardMigrationFacts, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-sqlite-guard-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public SqliteTenantGuardMigrationTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        builder.Services.AddAlvo();
        _services = builder.Services.BuildServiceProvider();
    }

    protected override IServiceProvider Services => _services;

    public void Dispose()
    {
        _services.Dispose();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
