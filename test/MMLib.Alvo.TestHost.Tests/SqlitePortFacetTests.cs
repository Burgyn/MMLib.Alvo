using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The data-port facet facts on SQLite — the engine that enforces no length and no enum, a temp file per fact.</summary>
public sealed class SqlitePortFacetTests : PortFacetFacts, IDisposable
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

    protected override Task<Action<IAlvoBuilder>> DatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-facets-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var connectionString = SqliteTempFile.ConnectionStringFor(path);

        return Task.FromResult<Action<IAlvoBuilder>>(alvo => alvo.UseSqlite(connectionString));
    }
}
