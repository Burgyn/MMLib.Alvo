using Microsoft.Data.Sqlite;

using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>The whole <see cref="DeliveryStoreContractTests"/> suite against a real SQLite database file, on the shipped connection string.</summary>
public sealed class SqliteDeliveryStoreTests : DeliveryStoreContractTests, IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-delivery-store-tests-{Guid.NewGuid():N}.db");

    protected override async Task<IDeliveryStoreWorld> WorldAsync() =>
        await DeliveryStoreWorld.StartAsync(() => new SqliteConnection($"Data Source={_databasePath};Pooling=False"));

    public void Dispose()
    {
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
