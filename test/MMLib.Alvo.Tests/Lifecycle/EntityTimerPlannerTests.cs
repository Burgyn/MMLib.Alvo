using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Lifecycle;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Lifecycle;

/// <summary>
/// A write's two images decide its timers: <c>due(row) = when(row) ? at(row) : null</c>; a create arms a due timer, an update
/// re-arms only a moved due, and a null due or a delete cancels by key.
/// </summary>
public sealed class EntityTimerPlannerTests
{
    private const string Correlation = "corr-1";

    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startsAt = _now.AddHours(48);
    private static readonly Guid _id = Guid.Parse("0193a0a0-0000-7000-8000-000000000001");
    private static readonly TenantId _tenant = new(Guid.Parse("22222222-0000-0000-0000-000000000002"));
    private static readonly IReadOnlyList<CompiledTimer> _timers = Compile();

    private static string Key => $"alvo:bookings:reminder:{_id:D}";

    [Fact]
    public void A_create_that_is_due_arms_at_its_time()
    {
        var change = Plan(previous: null, Row("confirmed", _startsAt)).ShouldHaveSingleItem();

        (change.Scope, change.Key).ShouldBe((_tenant.ToString(), Key));
        var arm = change.Arm.ShouldNotBeNull();
        (arm.Scope, arm.Key, arm.DueAt).ShouldBe((_tenant.ToString(), Key, _startsAt.AddHours(-24)));
    }

    [Fact]
    public void A_create_that_is_not_due_arms_nothing_and_cancels() =>
        Plan(previous: null, Row("draft", _startsAt)).ShouldHaveSingleItem().Arm.ShouldBeNull();

    [Fact]
    public void An_update_that_moves_the_due_re_arms() =>
        Plan(Row("confirmed", _startsAt), Row("confirmed", _startsAt.AddHours(24))).ShouldHaveSingleItem().Arm.ShouldNotBeNull()
            .DueAt.ShouldBe(_startsAt);

    [Fact]
    public void An_update_that_keeps_the_due_changes_nothing() =>
        Plan(Row("confirmed", _startsAt), Row("confirmed", _startsAt, note: "moved desk")).ShouldBeEmpty();

    [Fact]
    public void A_null_due_cancels()
    {
        var change = Plan(Row("confirmed", _startsAt), Row("confirmed", startsAt: null)).ShouldHaveSingleItem();

        (change.Key, change.Arm).ShouldBe((Key, null));
    }

    [Fact]
    public void Leaving_the_when_cancels() =>
        Plan(Row("confirmed", _startsAt), Row("cancelled", _startsAt)).ShouldHaveSingleItem().Arm.ShouldBeNull();

    [Fact]
    public void A_delete_cancels()
    {
        var change = Plan(Row("confirmed", _startsAt), stored: null).ShouldHaveSingleItem();

        (change.Scope, change.Key, change.Arm).ShouldBe((_tenant.ToString(), Key, null));
    }

    [Fact]
    public void The_fired_event_carries_entity_id_timer_and_at()
    {
        var arm = Plan(previous: null, Row("confirmed", _startsAt)).ShouldHaveSingleItem().Arm.ShouldNotBeNull();

        arm.MessageType.ShouldBe("bookings.reminder_due");
        var data = AlvoEventJson.Read(arm.Payload).Data.Record.ShouldNotBeNull();
        (data["entity"], data["id"], data["timer"], data["at"]).ShouldBe(("bookings", _id.ToString("D"), "reminder", "2026-11-07T09:00:00.0000000+00:00"));
    }

    [Fact]
    public void The_armed_timer_resumes_the_writer_s_correlation_and_is_caused_by_its_event()
    {
        var arm = Plan(previous: null, Row("confirmed", _startsAt)).ShouldHaveSingleItem().Arm.ShouldNotBeNull();

        var provenance = TimerProvenance.Read(arm.Provenance);
        (provenance.CorrelationId, provenance.CausationId).ShouldBe((Correlation, "event-1"));
    }

    [Fact]
    public void A_global_row_arms_in_the_empty_scope()
    {
        var global = Row("confirmed", _startsAt).With(AlvoManagedColumns.TenantId, null);

        var change = Plan(previous: null, global).ShouldHaveSingleItem();

        (change.Scope, change.Arm.ShouldNotBeNull().Scope).ShouldBe((string.Empty, string.Empty));
    }

    [Fact]
    public void A_writer_past_the_depth_limit_refuses_the_write()
    {
        var deep = AlvoContext.System(_tenant) with { Provenance = new AlvoProvenance { Depth = AlvoChainDepthLimit.DefaultMaxDepth + 1 } };

        Should.Throw<AlvoChainDepthExceededException>(() =>
            EntityTimerPlanner.Plan(_timers, Write(previous: null, Row("confirmed", _startsAt)) with { Caller = deep }, AlvoChainDepthLimit.Default));
    }

    [Fact]
    public void A_re_plan_arms_an_unarmed_row_whose_due_is_ahead() =>
        Replan(Row("confirmed", _startsAt)).ShouldHaveSingleItem().Arm.ShouldNotBeNull().DueAt.ShouldBe(_startsAt.AddHours(-24));

    [Fact]
    public void A_re_plan_leaves_a_timer_already_armed_at_its_due_alone() =>
        Replan(Row("confirmed", _startsAt), armedAt: _startsAt.AddHours(-24)).ShouldBeEmpty();

    [Fact]
    public void A_re_plan_moves_a_timer_armed_at_another_due() =>
        Replan(Row("confirmed", _startsAt), armedAt: _startsAt).ShouldHaveSingleItem().Arm.ShouldNotBeNull().DueAt.ShouldBe(_startsAt.AddHours(-24));

    [Fact]
    public void A_re_plan_cancels_an_armed_timer_whose_when_is_false()
    {
        var change = Replan(Row("cancelled", _startsAt), armedAt: _startsAt.AddHours(-24)).ShouldHaveSingleItem();

        (change.Scope, change.Key, change.Arm).ShouldBe((_tenant.ToString(), Key, null));
    }

    [Fact]
    public void A_re_plan_of_an_unarmed_row_whose_when_is_false_changes_nothing() =>
        Replan(Row("cancelled", _startsAt)).ShouldBeEmpty();

    [Fact]
    public void A_re_plan_does_not_arm_a_due_that_has_already_passed() =>
        Replan(Row("confirmed", _now.AddHours(12))).ShouldBeEmpty();

    [Fact]
    public void A_re_plan_reads_an_armed_timer_only_in_the_row_s_own_scope() =>
        Replan(Row("confirmed", _startsAt), armedAt: _startsAt.AddHours(-24), armedScope: string.Empty).ShouldHaveSingleItem().Arm.ShouldNotBeNull();

    [Fact]
    public void A_re_planned_timer_resumes_the_re_plan_s_flow()
    {
        var run = AlvoContext.System(null) with { Provenance = new AlvoProvenance { CorrelationId = "migration-run", CausationId = "migration-event", Depth = 1 } };

        var arm = Replan(Row("confirmed", _startsAt), caller: run).ShouldHaveSingleItem().Arm.ShouldNotBeNull();

        var provenance = TimerProvenance.Read(arm.Provenance);
        (provenance.CorrelationId, provenance.CausationId, provenance.Depth).ShouldBe(("migration-run", "migration-event", 1));
        arm.Scope.ShouldBe(_tenant.ToString());
    }

    [Fact]
    public void A_re_plan_past_the_depth_limit_is_refused()
    {
        var deep = AlvoContext.System(null) with { Provenance = new AlvoProvenance { Depth = AlvoChainDepthLimit.DefaultMaxDepth + 1 } };

        Should.Throw<AlvoChainDepthExceededException>(() => Replan(Row("confirmed", _startsAt), caller: deep));
    }

    [Fact]
    public void The_re_plan_keys_are_the_write_path_s_keys() =>
        EntityTimerPlanner.Keys(_timers, "bookings", _id).ShouldBe([Key]);

    private static IReadOnlyList<TimerChange> Plan(AlvoRecord? previous, AlvoRecord? stored) =>
        EntityTimerPlanner.Plan(_timers, Write(previous, stored), AlvoChainDepthLimit.Default);

    private static IReadOnlyList<TimerChange> Replan(
        AlvoRecord row, DateTimeOffset? armedAt = null, string? armedScope = null, AlvoContext? caller = null)
    {
        var armed = new Dictionary<(string Scope, string Key), DateTimeOffset>();
        if (armedAt is { } due)
        {
            armed[(armedScope ?? _tenant.ToString(), Key)] = due;
        }

        return EntityTimerPlanner.Replan(
            _timers, new EntityTimerReplan("bookings", row, armed, caller ?? AlvoContext.System(null), _now), AlvoChainDepthLimit.Default);
    }

    private static EntityTimerWrite Write(AlvoRecord? previous, AlvoRecord? stored) =>
        new("bookings", previous, stored, AlvoContext.System(_tenant), _now, Correlation, "event-1");

    private static AlvoRecord Row(string status, DateTimeOffset? startsAt, string note = "booked") => new(new Dictionary<string, object?>
    {
        [AlvoManagedColumns.Id] = _id,
        [AlvoManagedColumns.TenantId] = _tenant.Value,
        ["status"] = status,
        ["starts_at"] = startsAt,
        ["note"] = note,
    });

    private static IReadOnlyList<CompiledTimer> Compile()
    {
        var descriptor = AlvoDescriptor.Parse(
            """{"apiVersion": "alvo.dev/v1", "name": "timers", "tenancy": {"enabled": true}, "entities": {"bookings": {"tenancy": "scoped", "fields": {"""
            + """ "status": {"type": "enum", "values": ["draft", "confirmed", "cancelled", "done"], "default": "draft"},"""
            + """ "starts_at": {"type": "datetime"}, "note": {"type": "text"}},"""
            + """ "timers": {"reminder": {"at": "starts_at - duration('24h')", "when": "status == 'confirmed'", "event": "bookings.reminder_due"}},"""
            + """ "rules": {"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}}}}""");
        PolicyCatalog.TryBuild(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler(), out var catalog, out var errors)
            .ShouldBeTrue(string.Join(" | ", errors.Select(error => $"{error.Path}: {error.Message}")));
        catalog!.TryGetEntity("bookings", out var policy).ShouldBeTrue();

        return policy.Lifecycle.Timers;
    }
}
