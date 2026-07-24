using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Runs the full <see cref="DescriptorVersionStoreContractTests"/> suite against a real SQLite
/// database file, wired exclusively through the public <see cref="AlvoSqliteBuilderExtensions.UseSqlite"/>
/// entry point — the same path a host application would use, and the same fixture shape as
/// <see cref="SqliteSchemaMigratorTests"/>.
/// </summary>
public sealed class SqliteDescriptorVersionStoreTests : DescriptorVersionStoreContractTests, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-descriptor-versions-tests-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public SqliteDescriptorVersionStoreTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        _services = builder.Services.BuildServiceProvider();
    }

    protected override IDescriptorVersionStore CreateStore() => _services.GetRequiredService<IDescriptorVersionStore>();

    public void Dispose()
    {
        _services.Dispose();

        // Best-effort: the store disposes its (pooling-disabled) per-call connections above, which
        // is what actually releases the OS file handle. This is still a temp file either way, so a
        // stray lock (e.g. an antivirus scan on Windows) should not fail the test — the OS reclaims
        // temp files regardless.
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
