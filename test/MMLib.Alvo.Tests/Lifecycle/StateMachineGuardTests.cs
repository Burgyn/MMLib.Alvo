using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Lifecycle;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Rules;

namespace MMLib.Alvo.Tests.Lifecycle;

/// <summary>
/// The guard admits a state move only along a declared transition whose roles the caller holds and whose <c>when</c>
/// holds; anything else is a 422 <c>transition</c>, or a 403 when every matching transition needs a role the caller lacks.
/// </summary>
public sealed class StateMachineGuardTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly AlvoContext _member = Caller(Role.Authenticated);

    private static readonly AlvoContext _manager = Caller(Role.Authenticated, RoleCatalog.Create(["manager"]).Get("manager"));

    [Fact]
    public void An_update_that_keeps_the_state_is_not_a_transition() =>
        Should.NotThrow(() => Update(Machine(), from: "done", to: "done", _member));

    [Fact]
    public void An_undeclared_transition_is_refused_as_a_transition_violation()
    {
        var refusal = Should.Throw<AlvoConstraintViolationException>(() => Update(Machine(), from: "draft", to: "done", _manager));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Transition);
        refusal.Fields.ShouldBe(["status"]);
    }

    [Fact]
    public void A_transition_whose_when_is_false_is_refused()
    {
        Should.NotThrow(() => Update(Machine(), from: "draft", to: "confirmed", _member, startsAt: _now.AddDays(1)));
        Should.Throw<AlvoConstraintViolationException>(() => Update(Machine(), from: "draft", to: "confirmed", _member, startsAt: _now.AddDays(-1)))
            .Kind.ShouldBe(AlvoConstraintKind.Transition);
    }

    [Fact]
    public void A_transition_needing_a_role_the_caller_lacks_is_forbidden()
    {
        var refusal = Should.Throw<AlvoAuthorizationException>(() => Update(Machine(), from: "confirmed", to: "done", _member));

        refusal.Message.ShouldContain("'manager'");
        refusal.Message.ShouldContain("'close'");
        Should.NotThrow(() => Update(Machine(), from: "confirmed", to: "done", _manager));
    }

    [Fact]
    public void The_system_caller_takes_a_role_gated_transition_only_when_admin_is_listed()
    {
        Should.NotThrow(() => Update(Machine(), from: "done", to: "draft", AlvoContext.System(null)));
        Should.Throw<AlvoAuthorizationException>(() => Update(Machine(), from: "confirmed", to: "done", AlvoContext.System(null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cancelled")]
    [InlineData("legacy")]
    public void A_wildcard_transition_leaves_any_state_including_none(string? from) =>
        Should.NotThrow(() => Update(Machine(), from, to: "draft", AlvoContext.System(null)));

    [Fact]
    public void A_create_must_store_the_initial_state()
    {
        var machine = Machine(initial: "draft");

        Should.NotThrow(() => Create(machine, Row("draft")));
        Should.Throw<AlvoConstraintViolationException>(() => Create(machine, Row("confirmed"))).Kind.ShouldBe(AlvoConstraintKind.Transition);
    }

    [Fact]
    public void A_create_without_the_state_takes_the_default_as_initial()
    {
        Should.NotThrow(() => Create(Machine(initial: "draft"), Row(state: null, sendState: false)));
        Should.Throw<AlvoConstraintViolationException>(() => Create(Machine(initial: "draft", defaulted: false), Row(state: null, sendState: false)));
    }

    [Fact]
    public void A_create_into_a_machine_without_an_initial_admits_any_state() =>
        Should.NotThrow(() => Create(Machine(), Row("done")));

    [Fact]
    public void A_when_that_throws_refuses_the_write()
    {
        var explode = TestCelFunctions.Host(
            "explode", CelValueType.Bool, _ => throw new InvalidOperationException("boom"), TestCelFunctions.Parameter("s", CelValueType.String, nullable: true));
        var machine = Machine(functions: [explode]);

        Should.Throw<CelFunctionException>(() => Update(machine, from: "draft", to: "cancelled", _member));
    }

    [Fact]
    public void Any_one_matching_transition_admits_the_move()
    {
        Should.NotThrow(() => Update(Machine(), from: "confirmed", to: "cancelled", _member, note: "mine"));
        Should.NotThrow(() => Update(Machine(), from: "confirmed", to: "cancelled", _manager, note: "theirs"));
        Should.Throw<AlvoConstraintViolationException>(() => Update(Machine(), from: "confirmed", to: "cancelled", _member, note: "theirs"))
            .Kind.ShouldBe(AlvoConstraintKind.Transition);
    }

    [Fact]
    public void A_tenant_role_takes_a_role_gated_transition_of_a_scoped_entity() =>
        Should.NotThrow(() => Update(Dispatched(TenancyMode.Scoped), from: "draft", to: "done", TenantRoleCallers.Person));

    [Theory]
    [InlineData(TenancyMode.Global)]
    [InlineData(null)]
    public void A_tenant_role_never_takes_a_role_gated_transition_of_a_global_entity(TenancyMode? tenancy)
    {
        Should.Throw<AlvoAuthorizationException>(() => Update(Dispatched(tenancy), from: "draft", to: "done", TenantRoleCallers.Person));
        Should.NotThrow(() => Update(Dispatched(tenancy), from: "draft", to: "done", TenantRoleCallers.ProjectDispatcher));
    }

    [Fact]
    public void A_delete_is_no_transition() =>
        Should.NotThrow(() => StateMachineGuard.Ensure(Machine(), DataOperation.Delete, Row("done"), Row("done"), _member));

    [Fact]
    public void A_state_that_is_not_a_string_is_refused_rather_than_read_as_no_state()
    {
        var row = new AlvoRecord(new Dictionary<string, object?> { ["status"] = 1L });

        Should.Throw<InvalidOperationException>(() => StateMachineGuard.Ensure(Machine(), DataOperation.Update, row, row, AlvoContext.System(null)));
    }

    private static void Update(
        CompiledStateMachine machine, string? from, string? to, AlvoContext caller, DateTimeOffset? startsAt = null, string note = "x")
    {
        using var instant = AlvoFlow.EnterInstant(_now);
        StateMachineGuard.Ensure(machine, DataOperation.Update, Row(to, startsAt, note), Row(from, startsAt, note), caller);
    }

    private static void Create(CompiledStateMachine machine, AlvoRecord candidate)
    {
        using var instant = AlvoFlow.EnterInstant(_now);
        StateMachineGuard.Ensure(machine, DataOperation.Create, candidate, null, _member);
    }

    private static AlvoRecord Row(string? state, DateTimeOffset? startsAt = null, string note = "x", bool sendState = true)
    {
        var values = new Dictionary<string, object?> { ["starts_at"] = startsAt ?? _now.AddDays(1), ["note"] = note };
        if (sendState)
        {
            values["status"] = state;
        }

        return new AlvoRecord(values);
    }

    private static AlvoContext Caller(params Role[] roles) => new() { User = UserId.New(), Roles = roles.ToHashSet() };

    private static CompiledStateMachine Dispatched(TenancyMode? tenancy) =>
        new("status", null, null, [new CompiledTransition("dispatch", null, "done", TenantRoleCallers.Dispatcher, null)], tenancy);

    private static CompiledStateMachine Machine(string? initial = null, bool defaulted = true, params CelFunction[] functions)
    {
        var cancel = functions.Length > 0
            ? """ "cancel": {"from": ["draft"], "to": "cancelled", "when": "explode(note)"} """
            : """ "cancel": {"from": ["confirmed"], "to": "cancelled", "roles": ["manager"]}, "withdraw": {"from": ["confirmed"], "to": "cancelled", "when": "new.note == 'mine'"} """;
        var descriptor = AlvoDescriptor.Parse(
            """{"apiVersion": "alvo.dev/v1", "name": "guards", "auth": {"roles": ["manager"]}, "entities": {"bookings": {"fields": {"""
            + """ "status": {"type": "enum", "values": ["draft", "confirmed", "cancelled", "done"]""" + (defaulted ? """, "default": "draft"}""" : "}")
            + """, "starts_at": {"type": "datetime"}, "note": {"type": "string"}},"""
            + """ "stateMachine": {"field": "status", """ + (initial is null ? string.Empty : $"\"initial\": \"{initial}\", ")
            + """ "transitions": { "confirm": {"from": ["draft"], "to": "confirmed", "when": "new.starts_at > now()"},"""
            + """ "close": {"from": ["confirmed"], "to": "done", "roles": ["manager"]},"""
            + """ "reopen": {"from": ["*"], "to": "draft", "roles": ["admin"]}, """ + cancel + "}},"
            + """ "rules": {"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}}}}""");
        var catalog = PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), TestCelFunctions.Compiler(functions));
        catalog.TryGetEntity("bookings", out var policy).ShouldBeTrue();
        return policy.Lifecycle.StateMachine.ShouldNotBeNull();
    }
}
