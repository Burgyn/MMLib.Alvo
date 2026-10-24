using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Tests.Events;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>Durable work keyed by an id outlives the registration; the boot decides what becomes of it (spec 2026-10-14 E3).</summary>
/// <remarks>When the boot runs this step, and what it records, is <c>AlvoBootServiceStoredDescriptorTests</c>'s to pin.</remarks>
public sealed class ExtensionStateGuardTests : IDisposable
{
    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly FakeDeliveryStore _store = new(new ManualClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)));

    public ExtensionStateGuardTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unfinished_work_of_an_id_nobody_registers_refuses_the_start_naming_it_and_the_fix()
    {
        await PendingAsync("old-sync");

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => Guard(alvo => { }).RefuseOrphansAsync(Ct));

        refusal.Message.ShouldContain("'old-sync'");
        refusal.FixSuggestion.ShouldContain("AddRetiredExtension(\"old-sync\", AlvoRetirement.DeadLetter)");
        refusal.FixSuggestion.ShouldContain("AlvoRetirement.Discard");
    }

    [Fact]
    public async Task Every_orphaned_id_is_named_at_once()
    {
        await PendingAsync("old-sync");
        await PendingAsync("old-audit");

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => Guard(alvo => { }).RefuseOrphansAsync(Ct));

        refusal.Message.ShouldContain("'old-audit', 'old-sync'");
        refusal.FixSuggestion.ShouldContain("AddRetiredExtension(\"old-audit\", AlvoRetirement.DeadLetter)");
        refusal.FixSuggestion.ShouldContain("AddRetiredExtension(\"old-sync\", AlvoRetirement.DeadLetter)");
    }

    [Fact]
    public async Task Work_of_a_registered_handler_starts_normally()
    {
        await PendingAsync("on-created");

        await Guard(alvo => alvo.Entity("tickets", t => t.OnCreated("on-created", OnCreated))).RefuseOrphansAsync(Ct);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("pending");
    }

    [Fact]
    public async Task A_dead_letter_retirement_ends_the_work_as_dead_and_says_how_much()
    {
        await PendingAsync("old-sync");

        await Guard(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.DeadLetter)).RetireAsync(Ct);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("retired");
        _logs.Entries.ShouldContain(entry => entry.EventName == "ExtensionRetired" && entry.Message.Contains("old-sync"));
    }

    [Fact]
    public async Task A_discard_retirement_cancels_the_work()
    {
        await PendingAsync("old-sync");

        await Guard(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.Discard)).RetireAsync(Ct);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("cancelled");
    }

    [Fact]
    public async Task A_dead_delivery_is_history_and_does_not_block_a_start()
    {
        await _store.FanOutAsync(
            [new DeliveryRequest { EventId = Guid.NewGuid(), Subscriber = "old-sync", PartitionKey = "tickets:1", Kind = "handler", DeadReason = "when-failed" }],
            Ct);

        await Guard(alvo => { }).RefuseOrphansAsync(Ct);
    }

    [Fact]
    public async Task A_running_delivery_is_unfinished_work()
    {
        await PendingAsync("old-sync");
        (await _store.ClaimAsync(ClaimOf("old-sync"), Ct)).ShouldHaveSingleItem();

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("running");
        await Should.ThrowAsync<AlvoStartupRefusedException>(() => Guard(alvo => { }).RefuseOrphansAsync(Ct));
    }

    [Fact]
    public async Task A_retrying_delivery_is_unfinished_work()
    {
        await PendingAsync("old-sync");
        var claimed = (await _store.ClaimAsync(ClaimOf("old-sync"), Ct)).ShouldHaveSingleItem();
        (await _store.FailAsync(
            new DeliveryAttempt { Delivery = claimed, Claimant = "guard-tests", StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.Zero },
            new DeliveryFailure { ErrorType = "InvalidOperationException", RetryAfter = TimeSpan.FromMinutes(1), MaxAttempts = 7 },
            Ct)).ShouldBeTrue();

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("retrying");
        await Should.ThrowAsync<AlvoStartupRefusedException>(() => Guard(alvo => { }).RefuseOrphansAsync(Ct));
    }

    [Fact]
    public async Task The_orphan_check_only_reads_even_beside_a_retirement()
    {
        await PendingAsync("old-sync");
        await PendingAsync("forgotten");

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(
            () => Guard(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.DeadLetter)).RefuseOrphansAsync(Ct));

        refusal.Message.ShouldContain("'forgotten'");
        refusal.Message.ShouldNotContain("'old-sync'");
        _store.Rows.ShouldAllBe(row => row.Status == "pending");
    }

    [Fact]
    public async Task The_orphan_check_brings_up_a_store_that_builds_its_storage_in_EnsureAsync_before_reading_it()
    {
        var store = new StorageBuiltOnEnsure(_store);

        await new ExtensionStateGuard(Registry(alvo => { }), store, _loggers.CreateLogger<ExtensionStateGuard>()).RefuseOrphansAsync(Ct);

        store.Ensured.ShouldBeTrue();
    }

    [Fact]
    public async Task A_retirement_brings_up_a_store_that_builds_its_storage_in_EnsureAsync_before_writing_it()
    {
        var store = new StorageBuiltOnEnsure(_store);
        var guard = new ExtensionStateGuard(
            Registry(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.Discard)), store, _loggers.CreateLogger<ExtensionStateGuard>());

        await guard.RetireAsync(Ct);

        store.Ensured.ShouldBeTrue();
    }

    [Fact]
    public void Alvo_registers_the_guard_as_a_step_of_the_boot_not_as_a_hosted_service()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo();
        collection.AddAlvo();

        collection.Count(descriptor => descriptor.ServiceType == typeof(ExtensionStateGuard)).ShouldBe(1);
        collection.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ExtensionStateGuard));
    }

    private ExtensionStateGuard Guard(Action<IAlvoBuilder> configure) =>
        new(Registry(configure), _store, _loggers.CreateLogger<ExtensionStateGuard>());

    private static DeliveryClaim ClaimOf(string subscriber) => new()
    {
        Claimant = "guard-tests",
        Ordered = [],
        Unordered = [subscriber],
        BatchSize = 1,
        Lease = TimeSpan.FromMinutes(1),
        OutboxMaxAttempts = 5,
        OutboxClaimLease = TimeSpan.FromMinutes(1),
    };

    /// <summary>A third-party store that builds its storage in <see cref="IDeliveryStore.EnsureAsync"/>, as the port promises.</summary>
    private sealed class StorageBuiltOnEnsure(FakeDeliveryStore inner) : IDeliveryStore
    {
        internal bool Ensured { get; private set; }

        public Task EnsureAsync(CancellationToken cancellationToken = default)
        {
            Ensured = true;
            return Task.CompletedTask;
        }

        public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default) =>
            Built(() => inner.FanOutAsync(requests, cancellationToken));

        public Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default) =>
            Built(() => inner.ClaimAsync(claim, cancellationToken));

        public Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default) =>
            Built(() => inner.CompleteAsync(attempt, cancellationToken));

        public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default) =>
            Built(() => inner.FailAsync(attempt, failure, cancellationToken));

        public Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default) =>
            Built(() => inner.PendingSubscribersAsync(cancellationToken));

        public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
            Built(() => inner.DeadLetterPendingAsync(subscriber, reason, cancellationToken));

        public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
            Built(() => inner.CancelPendingAsync(subscriber, cancellationToken));

        private Task<T> Built<T>(Func<Task<T>> read) =>
            Ensured ? read() : throw new InvalidOperationException("no such table: alvo_deliveries");
    }

    private Task<int> PendingAsync(string subscriber) =>
        _store.FanOutAsync([new DeliveryRequest { EventId = Guid.NewGuid(), Subscriber = subscriber, PartitionKey = "tickets:1", Kind = "handler" }], Ct);
}
