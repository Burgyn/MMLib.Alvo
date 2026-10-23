using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Events;

/// <summary>Which handlers one committed event fans out to, and how each delivery starts life (spec 2026-10-14 §4.8).</summary>
public sealed class DeliveryFanOutTests : IDisposable
{
    private static readonly PolicyCatalog _catalog = HandlerCatalog.Catalog();

    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly FakeDeliveryStore _store = new(new ManualClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)));

    public DeliveryFanOutTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs).SetMinimumLevel(LogLevel.Debug));

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_matching_handler_gets_one_pending_delivery_and_an_unmatched_one_none()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t
            .OnUpdated("on-resolved", OnUpdated, when: "new.status == 'resolved'")
            .OnUpdated("on-open", OnUpdated, when: "new.status == 'open'")));

        (await fanOut.FanOutAsync(Event("entity.tickets.updated", Record(("status", "resolved")), Record(("status", "open"))), _catalog, Ct)).ShouldBe(1);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Subscriber.ShouldBe("on-resolved");
        row.Status.ShouldBe("pending");
        row.PartitionKey.ShouldBe("tickets:1");
    }

    [Fact]
    public async Task A_condition_that_throws_is_a_dead_when_failed_delivery()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("by-code", OnCreated, when: "int(new.code) > 0")));

        await fanOut.FanOutAsync(Event("entity.tickets.created", Record(("code", "x"))), _catalog, Ct);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("when-failed");
        _logs.Entries.ShouldContain(entry => entry.EventName == "HandlerConditionFailed" && entry.Level == LogLevel.Warning
            && entry.Message.Contains("Exception)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(7, "pending")]
    [InlineData(8, "dead")]
    public async Task An_event_at_the_depth_limit_fans_out_dead(int chainDepth, string status)
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("echo", OnCreated)));

        await fanOut.FanOutAsync(Event("entity.tickets.created", Record(("code", "1")), depth: chainDepth), _catalog, Ct);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe(status);
        if (status == "dead")
        {
            row.LastErrorType.ShouldBe("chain-depth-exceeded");
            _logs.Entries.ShouldContain(entry => entry.EventName == "ChainDepthExceeded" && entry.Level == LogLevel.Error);
        }
    }

    /// <summary>Pins the outcome only: the interpreter's null rule already makes this comparison false, gate or not.</summary>
    /// <remarks>
    /// <see cref="A_condition_reading_a_caller_value_the_event_lacks_fans_out_nothing"/> is the fact that needs the gate.
    /// </remarks>
    [Fact]
    public async Task A_condition_reading_a_tenant_the_event_lacks_fans_out_nothing()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("tenant-only", OnCreated, when: "new.id != @tenant.id")));

        (await fanOut.FanOutAsync(Event("entity.tickets.created", Record(("id", Guid.NewGuid()))), _catalog, Ct)).ShouldBe(0);

        _store.Rows.ShouldBeEmpty();
    }

    /// <summary>
    /// Each negation would hold against the stand-in an absent value reads as, so only the gate keeps these handlers out.
    /// </summary>
    [Theory]
    [InlineData("new.id != @user.id")]
    [InlineData("!('admin' in @user.roles)")]
    public async Task A_condition_reading_a_caller_value_the_event_lacks_fans_out_nothing(string when)
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("caller-only", OnCreated, when: when)));

        (await fanOut.FanOutAsync(Event("entity.tickets.created", Record(("id", Guid.NewGuid()))), _catalog, Ct)).ShouldBe(0);

        _store.Rows.ShouldBeEmpty();
        _logs.Entries.ShouldContain(entry => entry.EventName == "HandlerConditionUnread" && entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task A_storage_failure_escapes_so_the_dispatcher_can_hold_the_record_back()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("any", OnCreated)));
        _store.FanOutFailures = 1;

        await Should.ThrowAsync<InvalidOperationException>(() => fanOut.FanOutAsync(Event("entity.tickets.created", Record()), _catalog, Ct));

        _store.Rows.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("entity.tickets.created", 1)]
    [InlineData("entity.notes.updated", 1)]
    [InlineData("entity.tickets.deleted", 0)]
    public async Task A_star_on_saved_covers_every_entity_s_creates_and_updates(string type, int expected) =>
        (await FanOut(alvo => alvo.Entity("*", all => all.OnSaved("audit-all", OnSaved)))
            .FanOutAsync(Event(type, Record()), _catalog, Ct)).ShouldBe(expected);

    [Theory]
    [InlineData("orders.approved", 1)]
    [InlineData("orders.rejected", 0)]
    public async Task An_application_event_reaches_the_handlers_of_its_exact_name(string type, int expected) =>
        (await FanOut(alvo => alvo.AddEventHandler("on-approved", "orders.approved", OnRecord))
            .FanOutAsync(Event(type, Record()), _catalog, Ct)).ShouldBe(expected);

    /// <summary>
    /// A registered handler that does not fit the current catalog is never silently skipped (I3): every event its source
    /// selects gets a dead <c>unbound</c> delivery, visible and replayable.
    /// </summary>
    [Fact]
    public async Task An_event_a_handler_unbound_from_the_catalog_would_select_records_a_dead_unbound_delivery()
    {
        var fanOut = FanOut(alvo => alvo.Entity("notes", n => n.OnCreated("broken", OnCreated, when: "new.missing == 'x'")));

        (await fanOut.FanOutAsync(Event("entity.notes.created", Record()), _catalog, Ct)).ShouldBe(1);
        (await fanOut.FanOutAsync(Event("entity.tickets.created", Record()), _catalog, Ct)).ShouldBe(0, "its source does not select tickets");

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("unbound");
        _logs.Entries.Count(entry => entry.EventName == "ExtensionUnbound").ShouldBe(1, "logged once per catalog, not per event");
    }

    [Fact]
    public async Task A_delivery_carries_the_event_s_tenant()
    {
        var tenant = Guid.NewGuid().ToString();
        await FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("any", OnCreated)))
            .FanOutAsync(Event("entity.tickets.created", Record()) with { TenantId = tenant }, _catalog, Ct);

        _store.Rows.ShouldHaveSingleItem().TenantId.ShouldBe(tenant);
    }

    /// <summary>
    /// A dead delivery is announced when its row is written: an outbox retry re-fans the event out into rows that already
    /// exist, so it adds no second Error line and no second <c>alvo.delivery.dead</c> increment.
    /// </summary>
    [Fact]
    public async Task A_retried_fan_out_announces_a_dead_delivery_once()
    {
        var subscriber = UniqueId("loop");
        using var dead = new DeadDeliveries(subscriber);
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated(subscriber, OnCreated)));
        var deep = Event("entity.tickets.created", Record(), depth: AlvoChainDepthLimit.DefaultMaxDepth);

        (await fanOut.FanOutAsync(deep, _catalog, Ct)).ShouldBe(1);
        (await fanOut.FanOutAsync(deep, _catalog, Ct)).ShouldBe(1, "a retry still counts the event as fanned out");

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("dead");
        _logs.Entries.Count(entry => entry.EventName == "ChainDepthExceeded").ShouldBe(1);
        dead.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_dead_delivery_whose_insert_failed_is_not_announced()
    {
        var subscriber = UniqueId("unwritten");
        using var dead = new DeadDeliveries(subscriber);
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated(subscriber, OnCreated, when: "int(new.code) > 0")));
        _store.FanOutFailures = 1;

        await Should.ThrowAsync<InvalidOperationException>(() => fanOut.FanOutAsync(Event("entity.tickets.created", Record(("code", "x"))), _catalog, Ct));

        _logs.Entries.ShouldNotContain(entry => entry.EventName == "HandlerConditionFailed");
        dead.Count.ShouldBe(0);
    }

    /// <summary>The loop signal wins: a condition that throws on an event at the limit records the depth, not the condition.</summary>
    [Fact]
    public async Task The_depth_limit_outranks_a_condition_that_throws()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("by-code", OnCreated, when: "int(new.code) > 0")));

        await fanOut.FanOutAsync(Event("entity.tickets.created", Record(("code", "x")), depth: AlvoChainDepthLimit.DefaultMaxDepth), _catalog, Ct);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    [Fact]
    public async Task A_condition_that_does_not_hold_records_nothing_at_any_depth()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("never", OnCreated, when: "new.code == 'y'")));

        (await fanOut.FanOutAsync(Event("entity.tickets.created", Record(("code", "x")), depth: AlvoChainDepthLimit.DefaultMaxDepth), _catalog, Ct))
            .ShouldBe(0);

        _store.Rows.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("entity.tickets.created", true)]
    [InlineData("entity.tickets.updated", true)]
    [InlineData("entity.tickets.deleted", false)]
    public void Saved_selects_creates_and_updates(string type, bool selected) =>
        HandlerSources.Selects(new EntityEvents(["tickets"], HandlerVerb.Saved), type).Selected.ShouldBe(selected);

    [Fact]
    public void A_verb_this_build_does_not_know_selects_nothing() =>
        HandlerSources.Selects(new EntityEvents(["tickets"], (HandlerVerb)99), "entity.tickets.created").Selected.ShouldBeFalse();

    private static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private DeliveryFanOut FanOut(Action<IAlvoBuilder> configure) => new(
        _store,
        new ExtensionBinder(Registry(configure), CelFixtures.Compiler, EveryService(), _loggers.CreateLogger<ExtensionBinder>()),
        CelFixtures.Evaluator,
        AlvoChainDepthLimit.Default,
        _loggers.CreateLogger<DeliveryFanOut>());

    private static AlvoEvent Event(string type, AlvoRecord record, AlvoRecord? old = null, int depth = 0) => new()
    {
        Id = AlvoEventId.Create(DateTimeOffset.UtcNow),
        Source = AlvoEvent.DefaultSource,
        Type = type,
        Time = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero),
        Subject = "tickets/1",
        PartitionKey = "tickets:1",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "c",
        ChainDepth = depth,
        Data = new AlvoEventData { Record = record, OldRecord = old, Changed = ["status"] },
    };

    private static AlvoRecord Record(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal));
}
