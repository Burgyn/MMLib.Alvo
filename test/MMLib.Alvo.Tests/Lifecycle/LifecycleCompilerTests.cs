using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Lifecycle;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Lifecycle;

/// <summary>
/// The state machine and the timers compile into the policy catalog beside the before-hooks, against the same schema
/// revision, so a transition's roles and <c>when</c> and a timer's <c>at</c> and <c>when</c> fail at apply, never at a write.
/// </summary>
public sealed class LifecycleCompilerTests
{
    private const string Pointer = "/entities/bookings";

    [Fact]
    public void A_state_machine_and_timers_compile_into_the_entity_s_lifecycle()
    {
        var lifecycle = Compile(
            Machine(""" "confirm": {"from": ["draft"], "to": "confirmed", "roles": ["manager"]}, "reopen": {"from": ["*"], "to": "draft"} """, initial: "draft"),
            Timers(""" "visit-reminder": {"at": "starts_at - duration('24h')", "when": "status == 'confirmed'"} """)).Lifecycle;

        var machine = lifecycle.StateMachine.ShouldNotBeNull();
        (machine.Field, machine.Initial, machine.Default).ShouldBe(("status", "draft", "draft"));
        machine.Transitions.Select(transition => transition.Name).ShouldBe(["confirm", "reopen"]);
        machine.Transitions[0].From.ShouldNotBeNull().ShouldBe(["draft"]);
        machine.Transitions[0].Roles.ShouldNotBeNull().Select(role => role.Name).ShouldBe(["manager"]);
        machine.Transitions[1].From.ShouldBeNull();
        machine.Transitions[1].Roles.ShouldBeNull();
        var timer = lifecycle.Timers.ShouldHaveSingleItem();
        (timer.Name, timer.EventType, timer.At.ResultType).ShouldBe(("visit-reminder", "bookings.visit_reminder", CelValueType.Timestamp));
        timer.When.ShouldNotBeNull();
    }

    [Fact]
    public void A_transition_role_the_project_does_not_declare_is_refused()
    {
        var error = Errors(Machine(""" "close": {"from": ["confirmed"], "to": "cancelled", "roles": ["admin", "managr"]} """)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/stateMachine/transitions/close/roles/1");
        error.Message.ShouldContain("'managr'");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("'manager'");
    }

    [Fact]
    public void A_transition_when_that_is_not_a_boolean_is_refused()
    {
        var error = Errors(Machine(""" "confirm": {"from": ["draft"], "to": "confirmed", "when": "starts_at"} """)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/stateMachine/transitions/confirm/when");
        error.Message.ShouldContain("boolean");
    }

    [Fact]
    public void A_transition_when_naming_an_undeclared_role_is_refused() =>
        Errors(Machine(""" "confirm": {"from": ["draft"], "to": "confirmed", "when": "'managr' in @user.roles"} """))
            .ShouldHaveSingleItem().Path.ShouldBe(Pointer + "/stateMachine/transitions/confirm/when");

    [Fact]
    public void A_transition_when_may_read_old_new_and_changed()
    {
        var machine = Compile(Machine(
            """ "confirm": {"from": ["draft"], "to": "confirmed", "when": "old.status == 'draft' && new.starts_at > now() && changed(status) && 'manager' in @user.roles"} """)).Lifecycle.StateMachine;

        machine.ShouldNotBeNull().Transitions.ShouldHaveSingleItem().When.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("starts_at > now()", "Bool")]
    [InlineData("note", "String")]
    public void A_timer_at_must_be_a_timestamp(string at, string actual)
    {
        var error = Errors(Timers($$""" "reminder": {"at": "{{at}}"} """)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/timers/reminder/at");
        error.Message.ShouldContain("Timestamp");
        error.Message.ShouldContain(actual);
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("duration('24h')");
    }

    [Theory]
    [InlineData("starts_at - duration('24h')")]
    [InlineData("now() + duration('48h')")]
    [InlineData("starts_at")]
    public void A_timer_at_may_use_now_and_durations(string at) =>
        Compile(Timers($$""" "reminder": {"at": "{{at}}"} """)).Lifecycle.Timers.ShouldHaveSingleItem().At.ResultType.ShouldBe(CelValueType.Timestamp);

    [Theory]
    [InlineData("""{"at": "new.starts_at"}""", "/at")]
    [InlineData("""{"at": "old.starts_at"}""", "/at")]
    [InlineData("""{"at": "starts_at", "when": "changed(status)"}""", "/when")]
    [InlineData("""{"at": "starts_at", "when": "old.status == 'draft'"}""", "/when")]
    public void A_timer_expression_reading_old_new_or_changed_is_refused(string timer, string member)
    {
        var error = Errors(Timers($$""" "reminder": {{timer}} """)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/timers/reminder" + member);
        error.Message.ShouldContain("record's own fields");
    }

    [Theory]
    [InlineData("""{"at": "starts_at", "when": "'manager' in @user.roles"}""", "/when")]
    [InlineData("""{"at": "starts_at", "when": "site_id == @tenant.id"}""", "/when")]
    [InlineData("""{"at": "starts_at", "when": "site_id == @user.id"}""", "/when")]
    public void A_timer_expression_reading_the_caller_or_the_tenant_is_refused(string timer, string member)
    {
        var error = Errors(Timers($$""" "reminder": {{timer}} """)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/timers/reminder" + member);
        error.Message.ShouldContain("caller");
    }

    [Fact]
    public void A_compiled_at_evaluates_to_its_instant_or_to_none()
    {
        var at = Compile(Timers(""" "reminder": {"at": "starts_at - duration('24h')"} """)).Lifecycle.Timers.ShouldHaveSingleItem().At;
        var startsAt = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        CelInterpreter.EvaluateInstant(at, Row(startsAt), AlvoContext.System(null), now).ShouldBe(startsAt.AddHours(-24));
        CelInterpreter.EvaluateInstant(at, Row(null), AlvoContext.System(null), now).ShouldBeNull();
    }

    private static AlvoRecord Row(DateTimeOffset? startsAt) => new(new Dictionary<string, object?> { ["starts_at"] = startsAt });

    [Fact]
    public void An_entity_without_either_member_compiles_to_none() =>
        ReferenceEquals(Compile(string.Empty).Lifecycle, EntityLifecycle.None).ShouldBeTrue();

    private static string Machine(string transitions, string? initial = null) =>
        $$""", "stateMachine": {"field": "status"{{(initial is null ? "" : $", \"initial\": \"{initial}\"")}}, "transitions": {""" + transitions + "}}";

    private static string Timers(string timers) => """, "timers": {""" + timers + "}";

    private static EntityPolicy Compile(params string[] members)
    {
        var descriptor = Descriptor(members);
        PolicyCatalog.TryBuild(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler(), out var catalog, out var errors)
            .ShouldBeTrue(string.Join(" | ", errors.Select(error => $"{error.Path}: {error.Message}")));
        catalog!.TryGetEntity("bookings", out var policy).ShouldBeTrue();
        return policy;
    }

    private static IReadOnlyList<DescriptorValidationError> Errors(params string[] members)
    {
        var descriptor = Descriptor(members);
        PolicyCatalog.TryBuild(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler(), out _, out var errors).ShouldBeFalse();
        return errors;
    }

    private static AlvoDescriptor Descriptor(string[] members) => AlvoDescriptor.Parse(
        """{"apiVersion": "alvo.dev/v1", "name": "lifecycles", "auth": {"roles": ["manager"]}, "entities": {"bookings": {"fields": {"""
        + """ "status": {"type": "enum", "values": ["draft", "confirmed", "cancelled"], "default": "draft"},"""
        + """ "starts_at": {"type": "datetime"}, "note": {"type": "string"}, "site_id": {"type": "uuid"}}"""
        + string.Concat(members)
        + """, "rules": {"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}}}}""");
}
