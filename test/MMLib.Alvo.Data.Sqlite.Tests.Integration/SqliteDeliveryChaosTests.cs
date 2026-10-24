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
