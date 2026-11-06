using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Extensions;

using NSubstitute;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Scheduling;

[AlvoEvent("bookings.reminder_due")]
public sealed record BookingReminderDue(Guid BookingId, DateTimeOffset StartsAt);

public sealed record UnmarkedReminder(Guid BookingId);

/// <summary>What <see cref="IAlvoScheduler"/> writes: its checks, and the tenant, provenance and depth it alone decides.</summary>
public sealed class AlvoSchedulerTests
{
    private const string Reminder = "bookings.reminder_due";
    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId _tenant = new(Guid.NewGuid());

    private readonly ITimerStore _store = Substitute.For<ITimerStore>();
    private readonly List<TimerEntry> _armed = [];

    public AlvoSchedulerTests() =>
        _store.UpsertAsync(Arg.Do<TimerEntry>(_armed.Add), Arg.Any<CancellationToken>()).Returns(Guid.CreateVersion7());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_framework_event_type_cannot_be_scheduled()
    {
        await Should.ThrowAsync<ArgumentException>(() => Scheduler().ScheduleAsync("k", _now, "alvo.schedule.x", null, _tenant, Ct));

        _armed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Scheduling_above_the_depth_limit_is_refused()
    {
        using (AlvoFlow.Enter(new AlvoProvenance { Depth = 9 }))
        {
            await Should.ThrowAsync<AlvoChainDepthExceededException>(() => Scheduler().ScheduleAsync("k", _now, Reminder, null, _tenant, Ct));
        }

        using (AlvoFlow.Enter(new AlvoProvenance { Depth = 8 }))
        {
            await Scheduler().ScheduleAsync("k", _now, Reminder, null, _tenant, Ct);
        }

        TimerProvenance.Read(_armed.ShouldHaveSingleItem().Provenance).Depth.ShouldBe(8);
    }

    [Fact]
    public async Task A_timer_outside_any_flow_records_depth_zero_and_a_fresh_correlation()
    {
        await Scheduler().ScheduleAsync("a", _now, Reminder, null, _tenant, Ct);
        await Scheduler().ScheduleAsync("b", _now, Reminder, null, _tenant, Ct);

        var stored = _armed.Select(timer => TimerProvenance.Read(timer.Provenance)).ToList();
        stored.ShouldAllBe(provenance => provenance.Depth == 0 && provenance.CausationId == null);
        stored.Select(provenance => provenance.CorrelationId).ShouldAllBe(correlation => !string.IsNullOrEmpty(correlation));
        stored[0].CorrelationId.ShouldNotBe(stored[1].CorrelationId);
    }

    [Fact]
    public async Task A_refusal_is_a_faulted_task_not_a_synchronous_throw()
    {
        Task call;
        using (AlvoFlow.Enter(new AlvoProvenance { Depth = 9 }))
        {
            call = Scheduler().ScheduleAsync("k", _now, Reminder, null, _tenant, Ct);
        }

        call.IsFaulted.ShouldBeTrue();
        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => call);
        Scheduler(Scoped()).CancelAsync("k", cancellationToken: Ct).IsFaulted.ShouldBeTrue();
    }

    [Fact]
    public async Task A_timer_armed_under_a_W3C_trace_outside_any_flow_takes_the_trace_as_its_correlation()
    {
        using var activity = new System.Diagnostics.Activity("request").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();

        await Scheduler().ScheduleAsync("k", _now, Reminder, null, _tenant, Ct);

        var stored = TimerProvenance.Read(_armed.ShouldHaveSingleItem().Provenance);
        (stored.CorrelationId, stored.TraceParent).ShouldBe((activity.TraceId.ToString(), activity.Id));
    }

    [Fact]
    public async Task Before_the_schema_is_primed_a_timer_needs_a_stated_tenant()
    {
        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => Scheduler(primed: false).ScheduleAsync("k", _now, Reminder, null, cancellationToken: Ct));

        refusal.Message.ShouldContain("pass tenant:");
        await Scheduler(primed: false).ScheduleAsync("k", _now, Reminder, null, _tenant, Ct);
        _armed.ShouldHaveSingleItem().Scope.ShouldBe(_tenant.ToString());
    }

    [Fact]
    public async Task A_timer_inside_a_flow_records_its_correlation_causation_and_depth()
    {
        using (AlvoFlow.Enter(new AlvoProvenance { CorrelationId = "flow-1", CausationId = "event-1", Depth = 3 }))
        {
            await Scheduler().ScheduleAsync("k", _now, Reminder, null, _tenant, Ct);
        }

        var stored = TimerProvenance.Read(_armed.ShouldHaveSingleItem().Provenance);
        (stored.CorrelationId, stored.CausationId, stored.Depth).ShouldBe(("flow-1", "event-1", 3));
    }

    [Fact]
    public async Task A_scoped_schema_requires_the_tenant_outside_a_unit()
    {
        var refusal = await Should.ThrowAsync<ArgumentException>(() => Scheduler(Scoped()).ScheduleAsync("k", _now, Reminder, null, cancellationToken: Ct));

        refusal.Message.ShouldContain("pass tenant:");
        _armed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_global_schema_stores_a_timer_with_no_tenant_in_the_empty_scope()
    {
        await Scheduler().ScheduleAsync("k", _now, Reminder, null, cancellationToken: Ct);

        _armed.ShouldHaveSingleItem().Scope.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Inside_a_unit_the_timer_takes_the_unit_s_tenant_and_a_different_one_is_refused_and_poisons_it()
    {
        var unit = new FakeFlowUnit { Caller = AlvoContext.System(_tenant) };
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance { CorrelationId = "unit-1" });

        await Scheduler(Scoped()).ScheduleAsync("k", _now, Reminder, null, cancellationToken: Ct);
        await Should.ThrowAsync<ArgumentException>(() => Scheduler(Scoped()).ScheduleAsync("k", _now, Reminder, null, new TenantId(Guid.NewGuid()), Ct));

        _armed.ShouldHaveSingleItem().Scope.ShouldBe(_tenant.ToString());
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeOfType<ArgumentException>();
    }

    [Fact]
    public async Task Cancel_resolves_the_scope_like_schedule_and_answers_the_store()
    {
        _store.CancelAsync(_tenant.ToString(), "k", Arg.Any<CancellationToken>()).Returns(true);

        (await Scheduler(Scoped()).CancelAsync("k", _tenant, Ct)).ShouldBeTrue();
        await Should.ThrowAsync<ArgumentException>(() => Scheduler(Scoped()).CancelAsync("k", cancellationToken: Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tab\there")]
    public async Task A_key_must_be_one_to_two_hundred_printable_characters(string key)
    {
        await Should.ThrowAsync<ArgumentException>(() => Scheduler().ScheduleAsync(key, _now, Reminder, null, _tenant, Ct));
        await Should.ThrowAsync<ArgumentException>(() => Scheduler().ScheduleAsync(new string('k', 201), _now, Reminder, null, _tenant, Ct));
        await Scheduler().ScheduleAsync(new string('k', 200), _now, Reminder, null, _tenant, Ct);

        _armed.ShouldHaveSingleItem().Key.Length.ShouldBe(200);
    }

    [Fact]
    public async Task A_timer_is_stored_in_utc_under_the_timers_subject()
    {
        await Scheduler().ScheduleAsync("remind-1", new DateTimeOffset(2026, 11, 7, 11, 0, 0, TimeSpan.FromHours(2)), Reminder, null, _tenant, Ct);

        var timer = _armed.ShouldHaveSingleItem();
        timer.DueAt.ShouldBe(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
        timer.DueAt.Offset.ShouldBe(TimeSpan.Zero);
        AlvoEventJson.Read(timer.Payload).Subject.ShouldBe("timers/remind-1");
    }

    [Fact]
    public async Task The_typed_overload_takes_the_event_type_from_AlvoEvent_and_the_payload_from_the_record_shape()
    {
        var booking = Guid.NewGuid();

        await Scheduler().ScheduleAsync("k", _now, new BookingReminderDue(booking, _now.AddDays(1)), _tenant, Ct);

        var timer = _armed.ShouldHaveSingleItem();
        timer.MessageType.ShouldBe(Reminder);
        var data = AlvoEventJson.Read(timer.Payload).Data.Record.ShouldNotBeNull();
        data.Values.Keys.ShouldBe(["booking_id", "starts_at"], ignoreOrder: true);
        Guid.Parse((string)data["booking_id"]!).ShouldBe(booking);
    }

    [Fact]
    public async Task The_typed_overload_refuses_a_type_without_AlvoEvent()
    {
        var refusal = await Should.ThrowAsync<ArgumentException>(
            () => Scheduler().ScheduleAsync("k", _now, new UnmarkedReminder(Guid.NewGuid()), _tenant, Ct));

        refusal.Message.ShouldContain("[AlvoEvent(");
    }

    private AlvoScheduler Scheduler(PolicyCatalog? catalog = null, bool primed = true)
    {
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(primed ? catalog ?? HandlerCatalog.Catalog() : null);

        return new AlvoScheduler(_store, catalogs, AlvoChainDepthLimit.Default, new FakeTimeProvider(_now));
    }

    private static PolicyCatalog Scoped() => HandlerCatalog.CatalogOf(new SchemaModel(
        [.. HandlerCatalog.Schema.Entities.Select(entity => entity.Name == "notes" ? ScopedNotes(entity) : entity)]));

    private static EntitySchema ScopedNotes(EntitySchema notes) => notes with
    {
        Tenancy = TenancyMode.Scoped,
        Fields = [.. notes.Fields, new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid }],
    };
}
