using MMLib.Alvo.Testing.Scheduling;
using MMLib.Alvo.Tests.Data;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>The whole <see cref="TimerStoreContractTests"/> suite against PostgreSQL, a fresh database per test instance.</summary>
public sealed class PostgreSqlTimerStoreTests : TimerStoreContractTests, IClassFixture<PostgresFixture>
{
    private readonly string _connectionString = string.Empty;

    public PostgreSqlTimerStoreTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = $"alvo_timer_store_{Guid.NewGuid():N}";
        using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            command.ExecuteNonQuery();
        }

        _connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    protected override async Task<ITimerStoreWorld> WorldAsync() =>
        await TimerStoreWorld.StartAsync(() => new NpgsqlConnection(_connectionString));

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.");
}
