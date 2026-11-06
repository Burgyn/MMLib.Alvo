using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data.PostgreSql;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.TestHost;
using MMLib.Alvo.Tests.Data;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// What only PostgreSQL's concurrent writers can show about flow control: claimants racing one key's slots, two debounced
/// fan-outs racing one lane, two steps recorded at once, and two dispatchers sharing one tenant's cap. SQLite serialises its
/// writers, so none of these races exists there.
/// </summary>
public sealed class PostgreSqlConcurrencyRaceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Subscriber = "confirm-booking";
    private const string Tenant = "tenant-1";
    private const int Rounds = 20;

    private readonly List<string> _databases = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _databases)
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    [Fact]
    public async Task Racing_claimants_never_hold_more_slots_than_the_limit()
    {
        var world = await WorldAsync();
        for (var round = 0; round < Rounds; round++)
        {
            await FanOutAsync(world, round, deliveries: 6);

            var claimants = Enumerable.Range(0, 4).Select(claimant => $"c{round}-{claimant}").ToList();
            var claims = await Task.WhenAll(claimants.Select(claimant => Task.Run(() => world.Store.ClaimAsync(Capped(claimant), Ct), Ct)));

            claims.Sum(claimed => claimed.Count).ShouldBe(2, $"round {round}");
            (await world.ReadSlotsAsync()).Count(slot => slot.DeliveryId is not null).ShouldBe(2, $"round {round}");
            await CompleteAllAsync(world, claimants.Zip(claims));
        }
    }

    [Fact]
    public async Task Racing_debounced_fan_outs_of_one_lane_leave_only_the_newest_waiting()
    {
        var world = await WorldAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var lane = $"bookings:{round}";
            var older = await world.AppendEventAsync(lane);
            var newer = await world.AppendEventAsync(lane);

            await Task.WhenAll(
                Task.Run(() => world.Store.FanOutAsync([Debounced(older, lane)], Ct), Ct),
                Task.Run(() => world.Store.FanOutAsync([Debounced(newer, lane)], Ct), Ct));

            var statuses = (await world.Store.ListAsync(new DeliveryFilter { PartitionKey = lane, Limit = 10 }, Ct))
                .ToDictionary(delivery => delivery.EventId, delivery => delivery.Status);
            (statuses[older], statuses[newer]).ShouldBe(("superseded", "pending"), $"round {round}");
        }
    }

    [Fact]
    public async Task Racing_records_of_one_step_keep_one_row_and_return_its_result()
    {
        var world = await WorldAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var @event = await world.AppendEventAsync($"bookings:{round}");

            var recorded = await Task.WhenAll(Enumerable.Range(0, 4).Select(writer =>
                Task.Run(() => world.Store.RecordStepAsync(Step(@event, $"{writer}"), Ct), Ct)));

            recorded.Select(step => step.Result).Distinct().ShouldHaveSingleItem();
            (await world.Store.StepsAsync(@event, Subscriber, Ct)).ShouldHaveSingleItem().Result.ShouldBe(recorded[0].Result);
        }
    }

    /// <summary>
    /// Acceptance: two hosts on one database, a handler capped at two per tenant. The first host's two deliveries hold their
    /// places until the second host has drained once, so the second's claim runs against a full cap; then both drain at once.
    /// </summary>
    [Fact]
    public async Task Two_dispatchers_never_run_a_third_concurrent_delivery_of_one_tenant()
    {
        var connectionString = await DatabaseAsync();
        var log = new RunningLog();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
        await using var first = await HostAsync(connectionString, log, time);
        await using var second = await HostAsync(connectionString, log, time);
        var staff = AlvoTestHost.Caller("staff") with { Tenant = new TenantId(Guid.NewGuid()) };
        for (var booking = 0; booking < 8; booking++)
        {
            await first.Data.CreateAsync("bookings", Booking(booking, staff), staff, cancellationToken: Ct);
        }

        var held = first.DrainEventsAsync(Ct);
        await log.TwoRunning.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await second.DrainEventsAsync(Ct);
        log.Open();
        await held;
        await DrainBothUntilAllHandledAsync(first, second, log);

        log.MaxRunning.ShouldBe(2);
        log.Handled.ShouldBe(8);
    }

    private static async Task DrainBothUntilAllHandledAsync(AlvoTestHost first, AlvoTestHost second, RunningLog log)
    {
        for (var round = 0; round < 10 && log.Handled < 8; round++)
        {
            await Task.WhenAll(first.DrainEventsAsync(Ct), second.DrainEventsAsync(Ct));
        }
    }

    private static Task<AlvoTestHost> HostAsync(string connectionString, RunningLog log, TimeProvider time) =>
        AlvoTestHost.StartAsync(alvo =>
        {
            alvo.UsePostgreSql(connectionString);
            alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "booking-desk.alvo.json"));
            alvo.Entity("bookings", bookings => bookings.OnCreated(
                Subscriber,
                (AlvoCreated<AlvoRecord> _, CancellationToken ct) => log.RunAsync(ct),
                configure: options => options.Concurrency = AlvoConcurrency.PerKey("tenant", 2)));
        }, time, cancellationToken: Ct);

    private static Dictionary<string, object?> Booking(int booking, AlvoContext staff) => new()
    {
        ["note"] = $"b{booking}",
        ["tenant_id"] = staff.Tenant!.Value.Value,
    };

    private async Task<DeliveryStoreWorld> WorldAsync()
    {
        var connectionString = await DatabaseAsync();

        return await DeliveryStoreWorld.StartAsync(() => new NpgsqlConnection(connectionString), new PostgreSqlSqlDialect());
    }

    private static async Task FanOutAsync(DeliveryStoreWorld world, int round, int deliveries)
    {
        for (var delivery = 0; delivery < deliveries; delivery++)
        {
            var key = $"bookings:{round}:{delivery}";
            await world.Store.FanOutAsync(
                [new DeliveryRequest { EventId = await world.AppendEventAsync(key), Subscriber = Subscriber, PartitionKey = key, TenantId = Tenant, Kind = "handler" }], Ct);
        }
    }

    private static async Task CompleteAllAsync(DeliveryStoreWorld world, IEnumerable<(string Claimant, IReadOnlyList<ClaimedDelivery> Claimed)> claims)
    {
        foreach (var (claimant, claimed) in claims)
        {
            foreach (var delivery in claimed)
            {
                (await world.Store.CompleteAsync(Attempt(delivery, claimant), Ct)).ShouldBeTrue();
            }
        }
    }

    private static DeliveryClaim Capped(string claimant) => new()
    {
        Claimant = claimant,
        Ordered = [Subscriber],
        BatchSize = 10,
        Lease = TimeSpan.FromMinutes(1),
        OutboxMaxAttempts = 10,
        OutboxClaimLease = TimeSpan.FromMinutes(5),
        Limits = [new DeliveryLimit { Subscriber = Subscriber, Key = "tenant", Limit = 2 }],
    };

    private static DeliveryAttempt Attempt(ClaimedDelivery delivery, string claimant) =>
        new() { Delivery = delivery, Claimant = claimant, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromMilliseconds(1) };

    private static DeliveryRequest Debounced(Guid @event, string lane) =>
        new() { EventId = @event, Subscriber = Subscriber, PartitionKey = lane, TenantId = Tenant, Kind = "handler", Debounce = TimeSpan.FromSeconds(5) };

    private static DeliveryStep Step(Guid @event, string value) =>
        new() { EventId = @event, Subscriber = Subscriber, Name = "charge", Result = $"\"{value}\"", CompletedAt = DateTimeOffset.UnixEpoch };

    private async Task<string> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_concurrency_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// How many of the handler's deliveries run at once, observed on entry. The first two entrants wait at a gate until the
    /// fact opens it; a third entrant — the cap broken — would not wait, so it shows as a third concurrent run instead of a hang.
    /// </summary>
    private sealed class RunningLog
    {
        private readonly TaskCompletionSource _twoRunning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private int _maxRunning;
        private int _handled;

        internal Task TwoRunning => _twoRunning.Task;

        internal int MaxRunning => Volatile.Read(ref _maxRunning);

        internal int Handled => Volatile.Read(ref _handled);

        internal void Open() => _gate.TrySetResult();

        internal async Task RunAsync(CancellationToken ct)
        {
            var running = Interlocked.Increment(ref _running);
            RaiseMax(running);
            if (running == 2)
            {
                _twoRunning.TrySetResult();
            }

            try
            {
                if (running <= 2)
                {
                    await _gate.Task.WaitAsync(ct);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                Interlocked.Increment(ref _handled);
            }
        }

        private void RaiseMax(int running)
        {
            var seen = Volatile.Read(ref _maxRunning);
            while (running > seen && Interlocked.CompareExchange(ref _maxRunning, running, seen) != seen)
            {
                seen = Volatile.Read(ref _maxRunning);
            }
        }
    }
}
