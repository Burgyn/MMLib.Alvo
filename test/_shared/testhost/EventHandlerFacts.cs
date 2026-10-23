using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// Event handlers over the real pipeline (spec 2026-10-14 §7 HX-03), on whichever engine the deriving class supplies — the
/// SQLite leg in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class EventHandlerFacts
{
    protected const string HandlerDesk = "handler-desk.alvo.json";
    protected const string TenantDesk = "tenant-desk.alvo.json";
    protected const string Tickets = "tickets";

    private const string ForeignTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string ForeignTrace = $"00-{ForeignTraceId}-00f067aa0ba902b7-01";

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static AlvoContext Agent { get; } = AlvoTestHost.Caller("agent");

    /// <summary>(4) An event the condition does not match costs no delivery row.</summary>
    [Fact]
    public async Task An_unmatched_condition_records_no_delivery()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t
            .OnCreated("matches", Done, when: "new.title == 'match'")
            .OnCreated("misses", Done, when: "new.title == 'other'")));
        await CreateTicketAsync(alvo, "match");

        await alvo.DrainEventsAsync(Ct);

        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Subscriber.ShouldBe("matches");
        alvo.Hooks.AssertRan("matches", times: 1);
        alvo.Hooks.AssertRan("misses", times: 0);
    }

    /// <summary>(6) A webhook action that never succeeds does not keep a handler of the same event from running.</summary>
    [Fact]
    public async Task A_failing_webhook_action_does_not_keep_the_handler_from_its_delivery()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated("audits", Done)));
        await CreateTicketAsync(alvo, "audit me");

        var drained = await alvo.DrainEventsAsync(Ct);

        drained.Failed.ShouldBeGreaterThanOrEqualTo(1, "the webhook has no receiver, so its attempt fails");
        alvo.Hooks.AssertRan("audits", times: 1);
        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Status.ShouldBe("succeeded");
    }

    /// <summary>(8) A handler writing what it subscribes to, through its own data access, stops dead at the depth limit.</summary>
    [Fact]
    public async Task A_handler_writing_what_it_subscribes_to_through_its_context_stops_dead_at_the_depth_limit()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "echo", async (AlvoCreated<AlvoRecord> _, AlvoEventContext context, CancellationToken ct) =>
            {
                await context.Data.CreateAsync(Tickets, Values(("title", "echo")), ct);
            })));
        await CreateTicketAsync(alvo, "first");

        await alvo.DrainEventsAsync(Ct);

        await ShouldHaveStoppedAtTheLimitAsync(alvo, database, "echo");
    }

    /// <summary>(8) The same loop through a DI-injected data port and a fresh system context: the flow still carries the depth.</summary>
    [Fact]
    public async Task A_handler_writing_through_an_injected_data_port_with_a_fresh_system_context_stops_dead_too()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "echo-injected", async (AlvoCreated<AlvoRecord> _, IAlvoData data, CancellationToken ct) =>
            {
                await data.CreateAsync(Tickets, Values(("title", "echo")), AlvoContext.System(null), cancellationToken: ct);
            })));
        await CreateTicketAsync(alvo, "first");

        await alvo.DrainEventsAsync(Ct);

        await ShouldHaveStoppedAtTheLimitAsync(alvo, database, "echo-injected");
    }

    /// <summary>(16) A handler of an application event that publishes the same event stops at fan-out, with no write involved.</summary>
    [Fact]
    public async Task A_handler_republishing_its_own_event_stops_dead_at_the_depth_limit()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.AddEventHandler(
            "echo-approved",
            "orders.approved",
            (AlvoRecord _, AlvoEventContext context, IAlvoEvents events, CancellationToken ct) =>
                events.PublishAsync("orders.approved", "orders/1", null, context.Caller, ct)));
        await alvo.Services.GetRequiredService<IAlvoEvents>().PublishAsync("orders.approved", "orders/1", null, Agent, Ct);

        await alvo.DrainEventsAsync(Ct);

        var rows = await DeliveryRows.ReadAsync(database.Connect, Ct);
        rows.Count(row => row.Status == "succeeded").ShouldBe(8);
        rows.Single(row => row.Status == "dead").LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    /// <summary>(17) A condition that throws on one record's event is a dead delivery, and another record's event is not delayed.</summary>
    [Fact]
    public async Task A_condition_that_throws_is_dead_and_delays_no_other_record()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated("by-code", Done, when: "int(new.code) > 0")));
        await alvo.Data.CreateAsync(Tickets, Values(("title", "A"), ("code", "x")), Agent, cancellationToken: Ct);
        await alvo.Data.CreateAsync(Tickets, Values(("title", "B"), ("code", "5")), Agent, cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        var rows = await DeliveryRows.ReadAsync(database.Connect, Ct);
        rows[0].Status.ShouldBe("dead");
        rows[0].LastErrorType.ShouldBe("when-failed");
        rows[1].Status.ShouldBe("succeeded");
        alvo.Hooks.AssertRan("by-code", times: 1);
    }

    /// <summary>(19) A dead delivery holds back its record's later events for an ordered handler only.</summary>
    [Theory]
    [InlineData(AlvoOrdering.None, 1)]
    [InlineData(AlvoOrdering.PerRecord, 0)]
    public async Task A_dead_delivery_holds_back_its_record_only_for_an_ordered_handler(AlvoOrdering ordering, int laterRuns)
    {
        var database = await DatabaseAsync();
        var runs = 0;
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnSaved(
            "fragile",
            (AlvoSaved<AlvoRecord> saved) =>
            {
                if (Equals(saved.New["title"], "boom"))
                {
                    throw new InvalidOperationException("boom");
                }

                Interlocked.Increment(ref runs);
            },
            configure: options =>
            {
                options.Ordering = ordering;
                options.Retry = AlvoRetryPolicy.None;
            })));
        var ticket = await CreateTicketAsync(alvo, "boom");
        await alvo.DrainEventsAsync(Ct);

        await alvo.Data.UpdateAsync(Tickets, (Guid)ticket[AlvoManagedColumns.Id]!, Values(("title", "fine")), Agent, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        Volatile.Read(ref runs).ShouldBe(laterRuns);
    }

    /// <summary>(12) A system handler acts in its event's tenant: it cannot read another tenant's row, nor write into one.</summary>
    [Fact]
    public async Task A_system_handler_cannot_read_another_tenant_s_row()
    {
        var database = await DatabaseAsync();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var foreign = Guid.Empty;
        var seen = new ConcurrentDictionary<Guid, (bool ReadForeign, int Listed, bool WriteRefused)>();
        await using var alvo = await StartAsync(database, TenantDesk, builder => builder.Entity("notes", n => n.OnCreated(
            "tenant-peek",
            async (AlvoCreated<AlvoRecord> _, AlvoEventContext context, CancellationToken ct) =>
            {
                var own = context.Caller.Tenant!.Value.Value;
                var read = await context.Data.GetAsync("notes", foreign, ct);
                var listed = await context.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, ct);
                var other = own == tenantA ? tenantB : tenantA;
                var smuggled = await Record.ExceptionAsync(() => context.Data.CreateAsync("notes", Values(("text", "smuggled"), ("tenant_id", other)), ct));
                seen[own] = (read is not null, listed.Items.Count, smuggled is not null);
            },
            configure: options => options.RunAs = AlvoRunAs.System)));
        var noteB = await alvo.Data.CreateAsync("notes", Values(("text", "b"), ("tenant_id", tenantB)), Agent with { Tenant = new TenantId(tenantB) }, cancellationToken: Ct);
        foreign = (Guid)noteB[AlvoManagedColumns.Id]!;
        await alvo.Data.CreateAsync("notes", Values(("text", "a"), ("tenant_id", tenantA)), Agent with { Tenant = new TenantId(tenantA) }, cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        seen[tenantA].ShouldBe((false, 1, true), "tenant A's handler sees tenant A's one note, not tenant B's, and cannot write into tenant B");
        seen[tenantB].WriteRefused.ShouldBeTrue("tenant B's handler cannot write into tenant A either");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, Agent with { Tenant = new TenantId(tenantB) }, Ct)).Items.Count.ShouldBe(1);
    }

    /// <summary>(12) A system handler of a tenantless event holds no tenant, so a tenant-scoped entity is closed to it.</summary>
    [Fact]
    public async Task A_system_handler_of_a_tenantless_event_cannot_reach_a_tenant_scoped_entity()
    {
        var database = await DatabaseAsync();
        var refusals = new ConcurrentQueue<Exception?>();
        await using var alvo = await StartAsync(database, TenantDesk, builder => builder.AddEventHandler(
            "tenantless-reach",
            "desk.pinged",
            async (AlvoRecord _, AlvoEventContext context, CancellationToken ct) =>
            {
                refusals.Enqueue(await Record.ExceptionAsync(() => context.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, ct)));
                refusals.Enqueue(await Record.ExceptionAsync(() => context.Data.CreateAsync("notes", Values(("text", "smuggled"), ("tenant_id", Guid.NewGuid())), ct)));
            },
            configure: options => options.RunAs = AlvoRunAs.System));
        await alvo.Services.GetRequiredService<IAlvoEvents>().PublishAsync("desk.pinged", "desk/1", null, Agent, Ct);

        await alvo.DrainEventsAsync(Ct);

        alvo.Hooks.AssertRan("tenantless-reach", times: 1);
        refusals.Count.ShouldBe(2);
        refusals.ShouldAllBe(refusal => refusal is AlvoAuthorizationException && refusal.Message.Contains("tenant"), "the tenant guard denies before any rule");
    }

    /// <summary>
    /// (13) The span keeps the drain as its parent and links the envelope's trace, so the test host's recorder still counts
    /// the run under its drain.
    /// </summary>
    [Fact]
    public async Task A_handler_on_an_event_with_a_foreign_trace_still_counts_in_the_recorder()
    {
        var database = await DatabaseAsync();
        var handled = Guid.Empty;
        var spans = new ConcurrentQueue<Activity>();
        using var listener = DeliverySpans(spans);
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "traced", (AlvoCreated<AlvoRecord> _, AlvoEventContext context) => { handled = context.EventId; })));
        var traced = Agent with { Provenance = new AlvoProvenance { TraceParent = ForeignTrace } };
        await alvo.Data.CreateAsync(Tickets, Values(("title", "traced")), traced, cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        alvo.Hooks.AssertRan("traced", times: 1);
        var span = spans.Single(activity => Equals(activity.GetTagItem("alvo.event.id"), handled.ToString()));
        span.TraceId.ToHexString().ShouldNotBe(ForeignTraceId, "the envelope's trace is never the delivery's parent");
        span.ParentSpanId.ShouldNotBe(default, "the delivery runs under the drain");
        span.Links.ShouldContain(link => link.Context.TraceId.ToHexString() == ForeignTraceId);
    }

    /// <summary>A handler's write is one hop deeper than its event, caused by it, and in its correlation.</summary>
    [Fact]
    public async Task A_handler_s_write_is_caused_by_its_event_one_hop_deeper()
    {
        var database = await DatabaseAsync();
        var events = new ConcurrentDictionary<string, AlvoEvent>(StringComparer.Ordinal);
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "lineage",
            async (AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken ct) =>
            {
                var title = (string)created.New["title"]!;
                events[title] = context.Event;
                if (title == "parent")
                {
                    await context.Data.CreateAsync(Tickets, Values(("title", "child")), ct);
                }
            })));
        await CreateTicketAsync(alvo, "parent");

        await alvo.DrainEventsAsync(Ct);

        var parent = events["parent"];
        var child = events["child"];
        child.ChainDepth.ShouldBe(parent.ChainDepth + 1);
        child.CausationId.ShouldBe(parent.Id.ToString());
        child.CorrelationId.ShouldBe(parent.CorrelationId);
    }

    /// <summary>A write a handler makes past the depth limit is a dead delivery at once, never retried.</summary>
    [Fact]
    public async Task A_handler_write_past_the_depth_limit_is_dead_and_never_retried()
    {
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var runs = 0;
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "too-deep",
            async (AlvoCreated<AlvoRecord> _, AlvoEventContext context, IAlvoData data, CancellationToken ct) =>
            {
                Interlocked.Increment(ref runs);
                var deep = context.Caller with { Provenance = new AlvoProvenance { Depth = 9 } };
                await data.CreateAsync(Tickets, Values(("title", "deeper")), deep, cancellationToken: ct);
            })), clock);
        await CreateTicketAsync(alvo, "first");

        await alvo.DrainEventsAsync(Ct);
        clock.Advance(TimeSpan.FromDays(1));
        await alvo.DrainEventsAsync(Ct);

        var row = (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("chain-depth-exceeded");
        row.Attempts.ShouldBe(1);
        Volatile.Read(ref runs).ShouldBe(1, "a depth refusal is not retried");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Tickets }, Agent, Ct)).Items.Count.ShouldBe(1, "the refused write left nothing");
    }

    /// <summary>An event-actor handler acts as the caller who made the change, holding only the roles still declared.</summary>
    [Fact]
    public async Task An_event_actor_handler_acts_as_the_original_caller_with_only_declared_roles()
    {
        var database = await DatabaseAsync();
        var caller = AlvoTestHost.Caller("agent", "ghost");
        AlvoContext? actor = null;
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "as-caller",
            (AlvoCreated<AlvoRecord> _, AlvoEventContext context) => { actor = context.Caller; },
            configure: options => options.RunAs = AlvoRunAs.EventActor)));
        await alvo.Data.CreateAsync(Tickets, Values(("title", "mine")), caller, cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        actor.ShouldNotBeNull().User.ShouldBe(caller.User);
        actor.Roles.Select(role => role.Name).ShouldContain("agent");
        actor.Roles.Select(role => role.Name).ShouldNotContain("ghost", "a role the descriptor does not declare is not held");
        actor.Roles.Select(role => role.Name).ShouldNotContain("admin");
    }

    /// <summary>An envelope whose tenant cannot be read is a dead delivery — never a tenantless system run.</summary>
    [Fact]
    public async Task An_event_with_an_unreadable_tenant_is_dead_and_its_handler_never_runs()
    {
        var database = await DatabaseAsync();
        var runs = 0;
        await using var alvo = await StartAsync(database, TenantDesk, builder => builder.Entity("notes", n => n.OnCreated(
            "tenant-bound", (AlvoCreated<AlvoRecord> _) => { Interlocked.Increment(ref runs); })));
        var tenant = Guid.NewGuid();
        await alvo.Data.CreateAsync("notes", Values(("text", "a"), ("tenant_id", tenant)), Agent with { Tenant = new TenantId(tenant) }, cancellationToken: Ct);
        await CorruptTenantAsync(database);

        await alvo.DrainEventsAsync(Ct);

        Volatile.Read(ref runs).ShouldBe(0);
        var row = (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("event-unreadable");
    }

    /// <summary>
    /// (2) and (18): a process that stops answering mid-delivery leaves the claim to its lease; a restart delivers the same
    /// event again; and the first process finishing late changes nothing.
    /// </summary>
    [Fact]
    public async Task A_handler_killed_mid_delivery_runs_again_with_the_same_event_and_the_late_finisher_changes_nothing()
    {
        var database = await DatabaseAsync();
        var start = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var entered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid? rerun = null;
        var first = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "slow",
            async (AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
            {
                entered.TrySetResult(context.EventId);
                await release.Task;
            })), new FakeTimeProvider(start));
        await CreateTicketAsync(first, "Printer jam");
        var abandoned = first.DrainEventsAsync(Ct);
        var original = await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Status.ShouldBe("running", "the first process holds the claim it will never answer for in time");

        await using (var restarted = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "slow", (AlvoCreated<AlvoRecord> _, AlvoEventContext context) => { rerun = context.EventId; })), new FakeTimeProvider(start + TimeSpan.FromSeconds(91))))
        {
            await restarted.DrainEventsAsync(Ct);
        }

        release.SetResult();
        await abandoned;
        await first.DisposeAsync();

        rerun.ShouldBe(original, "at least once, and with the same event id every time");
        var row = (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem();
        row.Status.ShouldBe("succeeded");
        row.Attempts.ShouldBe(2);
        (await DeliveryRows.AttemptCountAsync(database.Connect, Ct)).ShouldBe(1, "the late finisher was fenced out and recorded nothing");
    }

    /// <summary>
    /// (5) across batches (B1): with one outbox entry per batch, event 1's fan-out fails and events 2 and 3 of the same
    /// record are fanned out first — yet none of them is delivered before event 1, and all arrive in order.
    /// </summary>
    [Fact]
    public async Task A_record_s_events_reach_its_handler_in_order_when_the_first_fan_out_fails_alone_in_its_batch()
    {
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var seen = new ConcurrentQueue<string>();
        await using var alvo = await StartAsync(database, HandlerDesk, builder =>
        {
            builder.Entity(Tickets, t => t.OnSaved("in-order", (AlvoSaved<AlvoRecord> saved) => seen.Enqueue((string)saved.New["title"]!)));
            builder.Services.Configure<AlvoEventOptions>(options => options.BatchSize = 1);
            FailingFirstFanOut.Install(builder.Services);
        }, clock);
        await WriteThreeVersionsAsync(alvo);

        await alvo.DrainEventsAsync(Ct);
        seen.ShouldBeEmpty("event 1 is not fanned out yet, so its record's later deliveries wait");
        clock.Advance(TimeSpan.FromMinutes(1));
        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe(["first", "second", "third"]);
    }

    /// <summary>
    /// (5) across a crash (B1): an outbox claimant that took event 1 and died before fanning it out holds the record's
    /// later deliveries back until its claim's lease returns event 1 to the queue.
    /// </summary>
    [Fact]
    public async Task A_record_s_events_wait_for_an_event_an_outbox_claimant_abandoned_before_its_fan_out()
    {
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var seen = new ConcurrentQueue<string>();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnSaved(
            "in-order", (AlvoSaved<AlvoRecord> saved) => seen.Enqueue((string)saved.New["title"]!))), clock);
        var ticket = await CreateTicketAsync(alvo, "first");
        await alvo.Services.GetRequiredService<IOutboxStore>().ClaimAsync("dead-dispatcher", batchSize: 1, maxAttempts: 10, TimeSpan.FromMinutes(5), Ct);
        await UpdateTitlesAsync(alvo, (Guid)ticket[AlvoManagedColumns.Id]!, "second", "third");

        await alvo.DrainEventsAsync(Ct);
        seen.ShouldBeEmpty("event 1 is held by a claimant that will never fan it out");
        clock.Advance(TimeSpan.FromMinutes(6));
        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe(["first", "second", "third"]);
    }

    /// <summary>(9) Unfinished work of a handler no longer registered refuses the start; a dead-letter retirement ends it as dead.</summary>
    [Fact]
    public async Task Work_left_by_a_dropped_handler_refuses_the_start_until_it_is_retired_as_dead()
    {
        var database = await DatabaseAsync();
        await LeaveRetryingWorkAsync(database);

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => StartAsync(database, HandlerDesk, _ => { }));
        refusal.Message.ShouldContain("'old-handler'");

        await using (await StartAsync(database, HandlerDesk, builder => builder.AddRetiredExtension("old-handler", AlvoRetirement.DeadLetter)))
        {
        }

        var row = (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("retired");
    }

    /// <summary>(9) A discard retirement cancels the work instead.</summary>
    [Fact]
    public async Task A_discard_retirement_cancels_the_work_a_dropped_handler_left()
    {
        var database = await DatabaseAsync();
        await LeaveRetryingWorkAsync(database);

        await using (await StartAsync(database, HandlerDesk, builder => builder.AddRetiredExtension("old-handler", AlvoRetirement.Discard)))
        {
        }

        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Status.ShouldBe("cancelled");
    }

    /// <summary>Registers this leg's database; both legs give every fact a database of its own that two hosts can share.</summary>
    protected abstract Task<HandlerDatabase> DatabaseAsync();

    protected static async Task<AlvoTestHost> StartAsync(
        HandlerDatabase database, string descriptor, Action<IAlvoBuilder> configure, TimeProvider? time = null) =>
        await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", descriptor));
                configure(alvo);
            },
            time,
            webhooks: null,
            Ct);

    protected static Dictionary<string, object?> Values(params (string Field, object? Value)[] values) =>
        values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal);

    private static Func<AlvoCreated<AlvoRecord>, Task> Done { get; } = _ => Task.CompletedTask;

    private static Task<AlvoRecord> CreateTicketAsync(AlvoTestHost alvo, string title) =>
        alvo.Data.CreateAsync(Tickets, Values(("title", title)), Agent, cancellationToken: Ct);

    private static async Task WriteThreeVersionsAsync(AlvoTestHost alvo)
    {
        var ticket = await CreateTicketAsync(alvo, "first");
        await UpdateTitlesAsync(alvo, (Guid)ticket[AlvoManagedColumns.Id]!, "second", "third");
    }

    private static async Task UpdateTitlesAsync(AlvoTestHost alvo, Guid id, params string[] titles)
    {
        foreach (var title in titles)
        {
            await alvo.Data.UpdateAsync(Tickets, id, Values(("title", title)), Agent, cancellationToken: Ct);
        }
    }

    private static ActivityListener DeliverySpans(ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "MMLib.Alvo.Hooks",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "alvo.delivery")
                {
                    spans.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        return listener;
    }

    private static async Task CorruptTenantAsync(HandlerDatabase database)
    {
        await using var connection = database.Connect();
        await connection.OpenAsync(Ct);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT id, payload FROM alvo_outbox";
        string id;
        JsonNode envelope;
        await using (var reader = await read.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            id = reader.GetString(0);
            envelope = JsonNode.Parse(reader.GetString(1))!;
            (await reader.ReadAsync(Ct)).ShouldBeFalse("one write, one outbox row");
        }

        envelope["tenantid"] = "not-a-tenant";
        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE alvo_outbox SET payload = @payload WHERE id = @id";
        AddParameter(write, "@payload", envelope.ToJsonString());
        AddParameter(write, "@id", id);
        (await write.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task ShouldHaveStoppedAtTheLimitAsync(AlvoTestHost alvo, HandlerDatabase database, string subscriber)
    {
        var rows = (await DeliveryRows.ReadAsync(database.Connect, Ct)).Where(row => row.Subscriber == subscriber).ToList();
        rows.Count(row => row.Status == "succeeded").ShouldBe(8, "events at depth 0 to 7 ran their handler, each writing one hop deeper");
        rows.Single(row => row.Status == "dead").LastErrorType.ShouldBe("chain-depth-exceeded");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Tickets }, Agent, Ct)).Items.Count.ShouldBe(9);
    }

    private static async Task LeaveRetryingWorkAsync(HandlerDatabase database)
    {
        await using var before = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "old-handler", (AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException()))));
        await CreateTicketAsync(before, "left behind");
        await before.DrainEventsAsync(Ct);
        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Status.ShouldBe("retrying");
    }

    /// <summary>How a leg hands a fact its database: the builder registration, and a raw connection to read the tables.</summary>
    protected sealed record HandlerDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);
}
