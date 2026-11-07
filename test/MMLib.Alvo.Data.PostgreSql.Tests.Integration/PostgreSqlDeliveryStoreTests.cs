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
public sealed class PostgreSqlDeliveryStoreTests : DeliveryStoreContractTests, IClassFixture<PostgresFixture>, IDisposable
{
    private const string Subscriber = "send-confirmation";
    private const int BacklogSize = 10_000;
    private const int BacklogBatch = 100;
    private const int FanOutChunk = 500;

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
    /// A receiver down long enough builds a backlog of due deliveries no claim has looked at yet — rows the planner has no
    /// statistics for. A claim over ten thousand of them, one lane each, must still answer promptly with the oldest batch:
    /// planned as a join without statistics, the lane check walked the subscriber's whole backlog once per row and ran past
    /// Npgsql's 30 s command timeout (#482).
    /// </summary>
    [Fact]
    public async Task A_claim_over_a_fresh_backlog_of_ten_thousand_deliveries_answers_promptly()
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();
        var events = await FanOutBacklogAsync(world, BacklogSize);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var claimed = await world.Store.ClaimAsync(ClaimFor("worker-1") with { BatchSize = BacklogBatch }, Ct);
        stopwatch.Stop();

        claimed.Select(delivery => delivery.EventId).ShouldBe(events.Take(BacklogBatch), "the claim takes the oldest batch, one head per lane");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "a claim over a fresh backlog must not walk it once per row");
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

    /// <summary>
    /// A replay naming its subscriber, and a replay naming none, each take the subscriber's advisory lock exclusively before
    /// the reset, so each waits while a claim holds it shared — and its lane check then sees what the claim committed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_replay_waits_while_a_claim_holds_a_subscriber_s_lock(bool named)
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();
        var @event = await world.AppendEventAsync("tickets:a");
        await world.Store.FanOutAsync(
            [new DeliveryRequest { EventId = @event, Subscriber = Subscriber, PartitionKey = "tickets:a", Kind = "handler", DeadReason = "when-failed" }], Ct);
        var filter = new DeliveryFilter { Subscriber = named ? Subscriber : null, Status = "dead", Limit = 20 };
        var entry = new DeliveryOperationEntry { Id = Guid.NewGuid(), At = DateTimeOffset.UnixEpoch, Actor = "ops", Operation = "replay", Target = Subscriber };

        var recorded = await world.WhileSubscriberLockedAsync(Subscriber, exclusive: false, () => world.Store.ReplayAsync(filter, entry, Ct));

        recorded.ShouldNotBeNull().Affected.ShouldBe(1);
    }

    /// <summary>
    /// A pause takes its subscriber's advisory lock exclusively before it writes the control, so it waits while a claim
    /// holds the lock shared: once the pause has returned, no claim statement that began before it can still take a batch.
    /// </summary>
    [Fact]
    public async Task A_pause_waits_while_a_claim_holds_its_subscriber_s_lock()
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();
        var entry = new DeliveryOperationEntry { Id = Guid.NewGuid(), At = DateTimeOffset.UnixEpoch, Actor = "ops", Operation = "pause", Target = Subscriber };

        var changed = await world.WhileSubscriberLockedAsync(Subscriber, exclusive: false, () => world.Store.SetPausedAsync(Subscriber, paused: true, entry, Ct));

        changed.ShouldBeTrue();
    }

    protected override async Task<IDeliveryStoreWorld> WorldAsync() => await ConcreteWorldAsync();

    /// <summary>
    /// Closes this fact's idle pooled connections: every fact opens its own database, so pools left idle across a suite of
    /// a hundred facts would exhaust the server's connection limit.
    /// </summary>
    public void Dispose()
    {
        if (_connectionString.Length > 0)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            NpgsqlConnection.ClearPool(connection);
        }

        GC.SuppressFinalize(this);
    }

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.");

    private Task<DeliveryStoreWorld> ConcreteWorldAsync() =>
        DeliveryStoreWorld.StartAsync(() => new NpgsqlConnection(_connectionString), new PostgreSqlSqlDialect());

    /// <summary>
    /// Fans out one delivery of <see cref="Subscriber"/> per event, each on a lane of its own, in statements small enough
    /// for PostgreSQL's parameter limit; answers the event ids in the order the claim takes them.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> FanOutBacklogAsync(DeliveryStoreWorld world, int count)
    {
        var events = Enumerable.Range(0, count)
            .Select(_ => AlvoEventId.Create(DateTimeOffset.UnixEpoch))
            .OrderBy(id => id.ToString(), StringComparer.Ordinal)
            .ToList();
        foreach (var chunk in events.Select((id, index) => (Id: id, Index: index)).Chunk(FanOutChunk))
        {
            await world.Store.FanOutAsync(
                [.. chunk.Select(@event => new DeliveryRequest
                {
                    EventId = @event.Id, Subscriber = Subscriber, PartitionKey = $"tickets:{@event.Index}", Kind = "webhook",
                })],
                Ct);
        }

        return events;
    }

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
