using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Tests.Events;

using System.Data.Common;

using Xunit;

namespace MMLib.Alvo.Data.Sqlite.Tests.Integration;

/// <summary>The SQLite leg of the delivery chaos criterion, over a real database file.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class SqliteDeliveryChaosTests : DeliveryChaosCriteriaTests, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"alvo-delivery-chaos-{Guid.NewGuid():N}.db");

    protected override string EngineDescription => "sqlite (file), " + BuildConfiguration;

    /// <summary>
    /// 64, the PostgreSQL leg's value, chosen by measurement: 64 and 128 tie within noise and are the fastest
    /// concurrency above one. Drain times for the 10 000 events, Debug build: 1 → ~130 s, 4 → 1 314 s,
    /// 16 (the shipped default) → 1 029 s, 64 → ~385 s, 128 → 377 s (PostgreSQL at 64: 5.6 s). None reaches the
    /// ~3-minute target; the SQLite throughput is tracked in issue 410.
    /// </summary>
    protected override int MaxConcurrency => 64;

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
        var connectionString = $"Data Source={_path}";

        return Task.FromResult<(Action<IAlvoBuilder>, Func<DbConnection>)>((alvo => alvo.UseSqlite(connectionString), () => new SqliteConnection(connectionString)));
    }
}
