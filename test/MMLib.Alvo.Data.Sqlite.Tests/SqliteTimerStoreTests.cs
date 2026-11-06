using Microsoft.Data.Sqlite;

using MMLib.Alvo.Testing.Scheduling;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>The whole <see cref="TimerStoreContractTests"/> suite against a real SQLite database file, on the shipped connection string.</summary>
public sealed class SqliteTimerStoreTests : TimerStoreContractTests, IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-timer-store-tests-{Guid.NewGuid():N}.db");

    protected override async Task<ITimerStoreWorld> WorldAsync() => await TimerStoreWorld.StartAsync(Connect);

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

    private SqliteConnection Connect() => new($"Data Source={_databasePath};Pooling=False");
}
