using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Ef;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>A declared rename's index names on SQLite (#523), over a database file of its own per fact.</summary>
public sealed class SqliteRenamedConstraintNameTests : RenamedConstraintNameFacts, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-sqlite-rename-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public SqliteRenamedConstraintNameTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        builder.Services.AddAlvo();
        _services = builder.Services.BuildServiceProvider();
    }

    protected override IServiceProvider Services => _services;

    protected override Task<IReadOnlyList<string>> IndexNamesAsync(string table) => CatalogQuery.NamesAsync(
        _services, "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = @table AND name NOT LIKE 'sqlite_autoindex%'", table);

    public void Dispose()
    {
        _services.Dispose();

        // Only this file's pool: a pooled handle keeps the file open on Windows, and clearing every pool disposes a handle
        // another fact is opening (see SqliteWriteGateTests).
        using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(connection);
        }

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
