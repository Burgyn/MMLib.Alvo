using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The handler facts on SQLite — a temp file per fact, so two hosts can share one database.</summary>
public sealed class SqliteEventHandlerTests : EventHandlerFacts, IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    protected override Task<HandlerDatabase> DatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-handlers-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var connectionString = $"Data Source={path}";

        return Task.FromResult(new HandlerDatabase(alvo => alvo.UseSqlite(connectionString), () => new SqliteConnection(connectionString)));
    }
}
