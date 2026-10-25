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
    private const string Subscriber = "send-confirmation";

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

    /// <summary>
    /// PostgreSQL can prove the claim implies the partial lane index's predicate, so its look at a lane head's earlier
    /// deliveries can seek the unfinished and dead rows only. An empty table plans a sequential scan whatever the indexes,
    /// so the fact turns that off and drops the full lane index: what is left to choose is whether the partial one applies.
    /// </summary>
    [Fact]
    public async Task The_lane_check_can_seek_the_partial_index_over_unfinished_deliveries()
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();

        var plan = await world.ExplainClaimAsync("EXPLAIN", "SET enable_seqscan = off", "DROP INDEX ix_alvo_deliveries_lane");

        plan.ShouldContain("ix_alvo_deliveries_lane_open on alvo_deliveries earlier");
    }

    /// <summary>
    /// A delivery put back to pending restarts its claim count at one, and the attempt that follows is recorded after the
    /// ones already in its history — the SQLite fact of the same name, on PostgreSQL.
    /// </summary>
    [Fact]
    public async Task An_attempt_after_a_reset_is_numbered_after_the_last_one()
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();
        var @event = await world.AppendEventAsync("tickets:a");
        await world.Store.FanOutAsync([new DeliveryRequest { EventId = @event, Subscriber = Subscriber, PartitionKey = "tickets:a", Kind = "handler" }], Ct);
        var first = (await world.Store.ClaimAsync(ClaimFor("worker-1"), Ct)).ShouldHaveSingleItem();
        (await world.Store.FailAsync(AttemptOf(first, "worker-1"), new DeliveryFailure { ErrorType = "Boom", MaxAttempts = 1 }, Ct)).ShouldBeTrue();

        await world.ResetAsync(first.Id);
        var second = (await world.Store.ClaimAsync(ClaimFor("worker-1"), Ct)).ShouldHaveSingleItem();

        second.Attempts.ShouldBe(1, "the claim count restarts with the reset");
        (await world.Store.CompleteAsync(AttemptOf(second, "worker-1"), Ct)).ShouldBeTrue();
        (await world.AttemptNumbersAsync(first.Id)).ShouldBe([1, 2]);
    }

    /// <summary>
    /// A claim takes each subscriber's advisory lock shared before its <c>UPDATE</c>, so it waits while an operator's reset
    /// of that subscriber holds the lock exclusively — and its snapshot then sees what the reset committed.
    /// </summary>
    [Fact]
    public async Task A_claim_waits_while_a_reset_holds_its_subscriber_s_lock()
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();
        var @event = await world.AppendEventAsync("tickets:a");
        await world.Store.FanOutAsync([new DeliveryRequest { EventId = @event, Subscriber = Subscriber, PartitionKey = "tickets:a", Kind = "handler" }], Ct);

        var claimed = await world.WhileSubscriberLockedAsync(Subscriber, exclusive: true, () => world.Store.ClaimAsync(ClaimFor("worker-1"), Ct));

        claimed.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A redeliver takes its subscriber's advisory lock exclusively before its <c>UPDATE</c>, so it waits while a claim
    /// holds the lock shared — and its lane check then sees what the claim committed.
    /// </summary>
    [Fact]
    public async Task A_redeliver_waits_while_a_claim_holds_its_subscriber_s_lock()
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();
        var @event = await world.AppendEventAsync("tickets:a");
        await world.Store.FanOutAsync(
            [new DeliveryRequest { EventId = @event, Subscriber = Subscriber, PartitionKey = "tickets:a", Kind = "handler", DeadReason = "when-failed" }], Ct);
        var id = (await world.Store.ListAsync(new DeliveryFilter { Limit = 1 }, Ct)).ShouldHaveSingleItem().Id;
        var entry = new DeliveryOperationEntry
        {
            Id = Guid.NewGuid(),
            At = DateTimeOffset.UnixEpoch,
            Actor = "ops",
            Operation = "redeliver",
            Target = id.ToString(),
        };

        var changed = await world.WhileSubscriberLockedAsync(Subscriber, exclusive: false, () => world.Store.RedeliverAsync(id, entry, Ct));

        changed.ShouldBeTrue();
    }

    protected override async Task<IDeliveryStoreWorld> WorldAsync() => await ConcreteWorldAsync();

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.");

    private Task<DeliveryStoreWorld> ConcreteWorldAsync() =>
        DeliveryStoreWorld.StartAsync(() => new NpgsqlConnection(_connectionString), new PostgreSqlSqlDialect());

    private static DeliveryClaim ClaimFor(string claimant) => new()
    {
        Claimant = claimant,
        Ordered = [Subscriber],
        BatchSize = 3,
        Lease = TimeSpan.FromMinutes(1),
        OutboxMaxAttempts = 10,
        OutboxClaimLease = TimeSpan.FromMinutes(5),
    };

    private static DeliveryAttempt AttemptOf(ClaimedDelivery delivery, string claimant) =>
        new() { Delivery = delivery, Claimant = claimant, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromMilliseconds(1) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
