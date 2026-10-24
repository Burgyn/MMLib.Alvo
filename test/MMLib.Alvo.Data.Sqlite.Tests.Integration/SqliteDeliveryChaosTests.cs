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
    /// 16, the shipped default, now that the store writes on one SQLite database queue at its write gate. Drain times for
    /// the 10 000 events, Debug build, before the gate → with it: 1 → ~130 s → 129.8 s, 16 → 1 029 s → 54.9 s,
    /// 64 → ~385 s → 48.4 s (PostgreSQL at 64: 5.6 s). 64 is a few seconds faster, but the leg runs what a host runs.
    /// Issue 410 tracks the rest of the gap.
    /// </summary>
    protected override int MaxConcurrency => 16;

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
