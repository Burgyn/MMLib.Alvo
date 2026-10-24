using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Tests.Events;

using System.Data.Common;

using Xunit;

namespace MMLib.Alvo.Data.Sqlite.Tests.Integration;

/// <summary>
/// The SQLite delivery chaos criterion with a handler that writes a record per event through its session: the data
/// path's writes competing with the dispatcher's own for the one write lock. Both queue at the database's write gate.
/// </summary>
/// <remarks>
/// Drain times for the 10 000 events at concurrency 16, Debug build: 1 098 s while the data path's writes bypassed the
/// gate, 83.9 s once they queued at it (the same run without handler writes: 56.0 s; with them at concurrency 1: 180.9 s).
/// </remarks>
[Collection(DispatchedEventCollection.Name)]
public sealed class SqliteDeliveryChaosWithHandlerWritesTests : DeliveryChaosCriteriaTests, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"alvo-delivery-chaos-writes-{Guid.NewGuid():N}.db");

    protected override string EngineDescription => "sqlite (file), " + BuildConfiguration;

    protected override int MaxConcurrency => 16;

    protected override bool HandlerWrites => true;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    protected override Task<(Action<IAlvoBuilder> Use, Func<DbConnection> Connect)> DatabaseAsync()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "SQLite's per-write cost on a Windows runner (~20 min for this run) is tracked by #410; the criterion is held on Linux.");

        var connectionString = $"Data Source={_path}";

        return Task.FromResult<(Action<IAlvoBuilder>, Func<DbConnection>)>((alvo => alvo.UseSqlite(connectionString), () => new SqliteConnection(connectionString)));
    }
}
