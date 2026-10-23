using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
using MMLib.Alvo.Tests.Events;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>Durable work keyed by an id outlives the registration; the boot decides what becomes of it (spec 2026-10-14 E3).</summary>
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

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => Guard(alvo => { }).StartingAsync(Ct));

        refusal.Message.ShouldContain("'old-sync'");
        refusal.FixSuggestion.ShouldContain("AddRetiredExtension(\"old-sync\", AlvoRetirement.DeadLetter)");
        refusal.FixSuggestion.ShouldContain("AlvoRetirement.Discard");
    }

    [Fact]
    public async Task Every_orphaned_id_is_named_at_once_and_the_boot_reads_as_failed()
    {
        await PendingAsync("old-sync");
        await PendingAsync("old-audit");
        var boot = ReadyBoot();

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(
            () => new ExtensionStateGuard(boot, Registry(alvo => { }), _store, Sequential, _loggers.CreateLogger<ExtensionStateGuard>()).StartingAsync(Ct));

        refusal.Message.ShouldContain("'old-audit', 'old-sync'");
        refusal.FixSuggestion.ShouldContain("AddRetiredExtension(\"old-audit\", AlvoRetirement.DeadLetter)");
        boot.Phase.ShouldBe(AlvoBootPhase.Failed);
        boot.Failure.ShouldBe(refusal.Message);
    }

    [Fact]
    public async Task Work_of_a_registered_handler_starts_normally()
    {
        await PendingAsync("on-created");

        await Guard(alvo => alvo.Entity("tickets", t => t.OnCreated("on-created", OnCreated))).StartingAsync(Ct);
    }

    [Fact]
    public async Task A_dead_letter_retirement_ends_the_work_as_dead_and_says_how_much()
    {
        await PendingAsync("old-sync");

        await Guard(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.DeadLetter)).StartingAsync(Ct);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("retired");
        _logs.Entries.ShouldContain(entry => entry.EventName == "ExtensionRetired" && entry.Message.Contains("old-sync"));
    }

    [Fact]
    public async Task A_discard_retirement_cancels_the_work()
    {
        await PendingAsync("old-sync");

        await Guard(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.Discard)).StartingAsync(Ct);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("cancelled");
    }

    [Fact]
    public async Task A_dead_delivery_is_history_and_does_not_block_a_start()
    {
        await _store.FanOutAsync(
            [new DeliveryRequest { EventId = Guid.NewGuid(), Subscriber = "old-sync", PartitionKey = "tickets:1", Kind = "handler", DeadReason = "when-failed" }],
            Ct);

        await Guard(alvo => { }).StartingAsync(Ct);
    }

    [Fact]
    public async Task A_boot_that_did_not_reach_ready_checks_nothing()
    {
        await PendingAsync("old-sync");
        var boot = new AlvoBootState();
        boot.Failed("refused for another reason");

        await new ExtensionStateGuard(boot, Registry(alvo => { }), _store, Sequential, _loggers.CreateLogger<ExtensionStateGuard>()).StartingAsync(Ct);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("pending");
    }

    [Fact]
    public async Task A_host_whose_boot_never_ran_starts_without_waiting_for_it()
    {
        await PendingAsync("old-sync");

        await new ExtensionStateGuard(new AlvoBootState(), Registry(alvo => { }), _store, Sequential, _loggers.CreateLogger<ExtensionStateGuard>())
            .StartingAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("pending");
    }

    [Fact]
    public async Task A_concurrent_start_waits_for_the_boot_to_settle_before_retiring()
    {
        await PendingAsync("old-sync");
        var boot = new AlvoBootState();
        var guard = new ExtensionStateGuard(
            boot, Registry(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.Discard)), _store, Concurrent,
            _loggers.CreateLogger<ExtensionStateGuard>());

        var starting = guard.StartingAsync(Ct);
        await Task.Delay(50, Ct);
        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("pending");
        boot.Ready("handlers", appliedRevision: 1);
        await starting.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("cancelled");
    }

    [Fact]
    public void Alvo_hosts_one_guard_after_the_boot_however_often_it_is_added()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo();
        collection.AddAlvo();

        var hosted = collection.Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType).ToList();

        hosted.Count(type => type == typeof(ExtensionStateGuard)).ShouldBe(1);
        hosted.IndexOf(typeof(ExtensionStateGuard)).ShouldBeGreaterThan(hosted.IndexOf(typeof(AlvoBootService)));
    }

    private ExtensionStateGuard Guard(Action<IAlvoBuilder> configure) =>
        new(ReadyBoot(), Registry(configure), _store, Sequential, _loggers.CreateLogger<ExtensionStateGuard>());

    private static IOptions<HostOptions> Sequential { get; } = Options.Create(new HostOptions());

    private static IOptions<HostOptions> Concurrent { get; } = Options.Create(new HostOptions { ServicesStartConcurrently = true });

    private static AlvoBootState ReadyBoot()
    {
        var boot = new AlvoBootState();
        boot.Ready("handlers", appliedRevision: 1);

        return boot;
    }

    private Task<int> PendingAsync(string subscriber) =>
        _store.FanOutAsync([new DeliveryRequest { EventId = Guid.NewGuid(), Subscriber = subscriber, PartitionKey = "tickets:1", Kind = "handler" }], Ct);
}
