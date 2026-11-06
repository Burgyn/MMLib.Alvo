using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The declarative-timer facts on SQLite — a temp file per fact.</summary>
public sealed class SqliteEntityTimerTests : EntityTimerFacts, IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            SqliteTempFile.Delete(file);
        }

        GC.SuppressFinalize(this);
    }

    protected override Task<LifecycleDatabase> DatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-timers-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var connectionString = SqliteTempFile.ConnectionStringFor(path);

        return Task.FromResult(new LifecycleDatabase(alvo => alvo.UseSqlite(connectionString), () => new SqliteConnection(connectionString)));
    }
}
