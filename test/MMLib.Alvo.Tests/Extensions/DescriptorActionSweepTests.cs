using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Events;
using MMLib.Alvo.Tests.Expressions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// The work of a descriptor action the serving catalog no longer binds is cancelled, once, with one operations-log row, and
/// nothing else is touched (issue 480). The apply, rollback and boot that run it are <c>AutomationFacts</c>' to pin.
/// </summary>
public sealed class DescriptorActionSweepTests : IDisposable
{
    private const string Stale = "/entities/tickets/hooks/afterCreate#00000000";
    private const string HostHandler = "ticket-sync";

    private const string Desk = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "desk",
          "webhooks": { "endpoints": { "erp": { "url": "https://erp.example/hooks", "secretRef": "erp" } } },
          "entities": {
            "tickets": {
              "fields": { "title": { "type": "string" } },
              "rules": { "list": "true", "get": "true", "create": "true", "update": "true", "delete": "true" },
              "hooks": { "afterCreate": [ { "action": { "type": "webhook", "endpoint": "erp" } } ] }
            }
          }
        }
        """;

    private static readonly DateTimeOffset _now = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly FakeDeliveryStore _store = new(new ManualClock(_now));
    private readonly PolicyCatalog _catalog = CatalogOf(Desk);

    public DescriptorActionSweepTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_action_the_catalog_no_longer_binds_has_its_work_cancelled_with_one_log_row_naming_the_revision()
    {
        await PendingAsync(Stale);
        await PendingAsync(Stale);

        await Sweep(_store).SweepAsync(_catalog, revision: 7, Ct);

        _store.Rows.ShouldAllBe(row => row.Status == "cancelled" && row.LastErrorType == "superseded");
        var logged = _store.Operations.ShouldHaveSingleItem();
        (logged.Operation, logged.Actor, logged.Target, logged.Affected, logged.At).ShouldBe(("cancel-superseded", "system", Stale, (int?)2, _now));
        logged.Reason.ShouldNotBeNull().ShouldContain("Revision 7");
    }

    [Fact]
    public async Task A_host_handler_and_an_action_the_catalog_binds_are_never_touched()
    {
        var bound = BoundAction();
        await PendingAsync(HostHandler);
        await PendingAsync(bound);

        await Sweep(_store).SweepAsync(_catalog, revision: 2, Ct);

        _store.Rows.ShouldAllBe(row => row.Status == "pending");
        _store.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_sweep_cancels_nothing_and_logs_nothing()
    {
        await PendingAsync(Stale);
        var sweep = Sweep(_store);

        await sweep.SweepAsync(_catalog, revision: 2, Ct);
        await sweep.SweepAsync(_catalog, revision: 3, Ct);

        _store.Operations.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_store_without_the_internal_member_still_cancels_the_work_through_the_port()
    {
        var store = Substitute.For<IDeliveryStore>();
        store.PendingSubscribersAsync(Arg.Any<CancellationToken>()).Returns([Stale, HostHandler]);
        store.CancelPendingAsync(Stale, Arg.Any<CancellationToken>()).Returns(1);

        await Sweep(store).SweepAsync(_catalog, revision: 2, Ct);

        await store.Received(1).CancelPendingAsync(Stale, Arg.Any<CancellationToken>());
        await store.DidNotReceive().CancelPendingAsync(HostHandler, Arg.Any<CancellationToken>());
        _logs.Warnings.ShouldContain(line => line.Contains("cancel-superseded") && line.Contains(Stale));
    }

    [Fact]
    public async Task A_failing_store_is_logged_and_never_fails_the_caller()
    {
        var store = Substitute.For<IDeliveryStore>();
        store.PendingSubscribersAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("db down"));

        await Should.NotThrowAsync(() => Sweep(store).SweepAsync(_catalog, revision: 2, Ct));

        _logs.Warnings.ShouldContain(line => line.Contains("TimeoutException") && !line.Contains("db down"));
    }

    [Fact]
    public void Alvo_registers_one_sweep()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo();
        collection.AddAlvo();

        collection.Count(descriptor => descriptor.ServiceType == typeof(DescriptorActionSweep)).ShouldBe(1);
    }

    private DescriptorActionSweep Sweep(IDeliveryStore store) =>
        new(Binder(), new ManualClock(_now), _loggers.CreateLogger<DescriptorActionSweep>(), store);

    private ExtensionBinder Binder() =>
        new(Registry(_ => { }), CelFixtures.Compiler, EveryService(), _loggers.CreateLogger<ExtensionBinder>());

    private string BoundAction() =>
        Binder().Result(_catalog).Bindings.Handlers.Select(handler => handler.Id).Single(id => id.StartsWith('/'));

    private static PolicyCatalog CatalogOf(string json)
    {
        var descriptor = AlvoDescriptor.Parse(json);
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), CelFixtures.Compiler);
    }

    private Task<int> PendingAsync(string subscriber) =>
        _store.FanOutAsync([new DeliveryRequest { EventId = Guid.NewGuid(), Subscriber = subscriber, PartitionKey = "tickets:1", Kind = "action" }], Ct);
}
