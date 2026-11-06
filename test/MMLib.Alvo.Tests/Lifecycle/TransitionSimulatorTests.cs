using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Lifecycle;
using MMLib.Alvo.Management;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;

namespace MMLib.Alvo.Tests.Lifecycle;

/// <summary>
/// The policy simulator answers, per transition of an entity's state machine, whether the simulated caller holds a role
/// it lists — and leaves a transition whose <c>when</c> reads the record undecided, because the simulator scores no record.
/// </summary>
public sealed class TransitionSimulatorTests
{
    private const string Machine =
        """
        , "stateMachine": {"field": "status", "initial": "draft", "transitions": {
            "confirm": {"from": ["draft"], "to": "confirmed"},
            "close": {"from": ["confirmed", "cancelled"], "to": "done", "roles": ["manager"]},
            "cancel": {"from": ["*"], "to": "cancelled", "roles": ["staff"], "when": "new.starts_at > now()"}}}
        """;

    [Fact]
    public void A_role_outside_the_transition_is_not_allowed() =>
        Verdict(Machine, "update", "staff", "close").Allowed.ShouldBe(false);

    [Fact]
    public void A_listed_role_is_allowed()
    {
        var close = Verdict(Machine, "update", "manager", "close");

        close.Allowed.ShouldBe(true);
        close.From.ShouldBe(["cancelled", "confirmed"]);
        close.Roles.ShouldBe(["manager"]);
        (close.To, close.When).ShouldBe(("done", null));
        Verdict(Machine, "update", "manager", "confirm").Allowed.ShouldBe(true);
    }

    [Fact]
    public void A_transition_with_when_is_decided_per_record()
    {
        var cancel = Verdict(Machine, "update", "staff", "cancel");

        cancel.Allowed.ShouldBeNull();
        cancel.From.ShouldBe(["*"]);
        cancel.When.ShouldBe("new.starts_at > now()");
        Verdict(Machine, "update", "manager", "cancel").Allowed.ShouldBe(false);
    }

    [Fact]
    public void Transitions_are_listed_in_declaration_order() =>
        Simulate(Machine, "update", "staff").Select(verdict => verdict.Name).ShouldBe(["confirm", "close", "cancel"]);

    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("list")]
    public void Only_an_update_lists_transitions(string operation) =>
        Simulate(Machine, operation, "staff").ShouldBeEmpty();

    [Fact]
    public void An_entity_without_a_machine_lists_none() =>
        Simulate(string.Empty, "update", "staff").ShouldBeEmpty();

    [Fact]
    public void An_unknown_entity_or_an_unprimed_catalog_lists_none()
    {
        new TransitionSimulator(new PolicyCatalogProvider()).Simulate("bookings", "update", Caller("staff")).ShouldBeEmpty();
        Simulator(Machine).Simulate("rooms", "update", Caller("staff")).ShouldBeEmpty();
    }

    private static ManagementTransitionVerdict Verdict(string members, string operation, string role, string transition) =>
        Simulate(members, operation, role).Single(verdict => verdict.Name == transition);

    private static IReadOnlyList<ManagementTransitionVerdict> Simulate(string members, string operation, string role) =>
        Simulator(members).Simulate("bookings", operation, Caller(role));

    private static TransitionSimulator Simulator(string members)
    {
        var provider = new PolicyCatalogProvider();
        var descriptor = AlvoDescriptor.Parse(
            """{"apiVersion": "alvo.dev/v1", "name": "simulated", "auth": {"roles": ["staff", "manager"]}, "entities": {"bookings": {"fields": {"""
            + """ "status": {"type": "enum", "values": ["draft", "confirmed", "cancelled", "done"], "default": "draft"}, "starts_at": {"type": "datetime"}}"""
            + members
            + """, "rules": {"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}}}}""");
        provider.SetCurrent("simulated", PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler()));
        return new TransitionSimulator(provider);
    }

    private static AlvoContext Caller(string role) => new()
    {
        User = new UserId(Guid.NewGuid()),
        Roles = new HashSet<Role> { Role.Authenticated, Role.Application(role) },
    };
}
