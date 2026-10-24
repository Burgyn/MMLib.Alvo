using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The typed-layer facts on SQLite — a temp file per fact, its name the marker the SQL capture matches.</summary>
public sealed class SqliteTypedLayerTests : TypedLayerFacts, IDisposable
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

    protected override Task<TypedDatabase> DatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-typed-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var connectionString = $"Data Source={path}";

        return Task.FromResult(new TypedDatabase(alvo => alvo.UseSqlite(connectionString), Path.GetFileName(path)));
    }
}
