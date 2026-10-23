using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Data;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The whole <see cref="DeliveryStoreContractTests"/> suite against PostgreSQL, a fresh database per test instance, and
/// the one fact a shape assertion cannot reach: two claimants racing one lane head.
/// </summary>
public sealed class PostgreSqlDeliveryStoreTests : DeliveryStoreContractTests, IClassFixture<PostgresFixture>
{
    private readonly string _connectionString = string.Empty;

    public PostgreSqlDeliveryStoreTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = $"alvo_delivery_store_{Guid.NewGuid():N}";
        using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            command.ExecuteNonQuery();
        }

        _connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    [Fact]
    public async Task A_second_claimant_claims_nothing_rather_than_the_same_lane_head()
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();
        var @event = await world.AppendEventAsync("tickets:a");
        await world.Store.FanOutAsync(
            [new DeliveryRequest { EventId = @event, Subscriber = "send-confirmation", PartitionKey = "tickets:a", Kind = "handler" }],
            TestContext.Current.CancellationToken);

        var (first, second) = await world.TwoConcurrentClaimsAsync("send-confirmation");

        first.ShouldBe(1);
        second.ShouldBe(0, "the outer condition re-checks status after the lock clears; without it the row is claimed twice");
    }

    protected override async Task<IDeliveryStoreWorld> WorldAsync() => await ConcreteWorldAsync();

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.");

    private Task<DeliveryStoreWorld> ConcreteWorldAsync() =>
        DeliveryStoreWorld.StartAsync(() => new NpgsqlConnection(_connectionString));
}
