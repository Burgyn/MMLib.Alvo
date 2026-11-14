using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The caller as at the event (spec 2026-10-14 E8), rebuilt from the envelope for <c>AlvoRunAs.EventActor</c> — its tenant
/// roles only those the applied descriptor still lets a membership carry (S3 plan D10a).
/// </summary>
public sealed class EventActorTests
{
    private static readonly Guid _user = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _tenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void The_actor_is_the_recorded_caller_with_the_roles_it_still_holds_by_declaration()
    {
        var actor = EventActor.Of(Event(_user.ToString(), "{\"roles\":[\"agent\",\"authenticated\",\"retired-role\"]}"), RoleCatalog.Create(["agent"]), SelfServicePolicy.None);

        actor.User.Value.ShouldBe(_user);
        actor.Tenant.ShouldBe(new TenantId(_tenant));
        actor.Roles.Select(role => role.Name).Order(StringComparer.Ordinal).ShouldBe(["agent", "authenticated"]);
        actor.ActorKind.ShouldBe(AlvoActorKind.User);
    }

    [Fact]
    public void An_actor_without_a_readable_role_holds_only_anon() =>
        EventActor.Of(Event(_user.ToString(), authClaims: null), RoleCatalog.BuiltInOnly, SelfServicePolicy.None).Roles.ShouldBe([Role.Anon]);

    [Fact]
    public void An_actor_whose_every_recorded_role_is_no_longer_declared_holds_only_anon() =>
        EventActor.Of(Event(_user.ToString(), "{\"roles\":[\"retired-role\"]}"), RoleCatalog.BuiltInOnly, SelfServicePolicy.None).Roles.ShouldBe([Role.Anon]);

    [Fact]
    public void An_anonymous_change_has_an_anonymous_actor_in_its_tenant()
    {
        var actor = EventActor.Of(Event(authId: null, authClaims: null), RoleCatalog.BuiltInOnly, SelfServicePolicy.None);

        actor.User.ShouldBe(default);
        actor.Tenant.ShouldBe(new TenantId(_tenant));
    }

    [Fact]
    public void An_envelope_without_a_credential_never_lends_its_recorded_roles_to_an_anonymous_actor() =>
        EventActor.Of(Event(authId: null, "{\"roles\":[\"admin\"]}"), RoleCatalog.BuiltInOnly, SelfServicePolicy.None).Roles.ShouldBe([Role.Anon]);

    [Fact]
    public void The_actor_keeps_the_still_declared_tenant_roles_of_its_tenant()
    {
        var actor = EventActor.Of(
            Event(_user.ToString(), """{"roles":["authenticated"],"tenantRoles":["dispatcher","retired-role"]}"""),
            RoleCatalog.Create(["dispatcher"]),
            Assignable("dispatcher"));

        actor.Tenant.ShouldBe(new TenantId(_tenant));
        actor.Roles.ShouldBe([Role.Authenticated]);
        actor.TenantRoles.Select(role => role.Name).ShouldBe(["dispatcher"]);
        EventActor.Of(Event(_user.ToString(), """{"roles":["authenticated"],"tenantRoles":["retired-role"]}"""), RoleCatalog.BuiltInOnly, Assignable("dispatcher"))
            .TenantRoles.ShouldBeEmpty("unlike the roles, no tenant role stands in for none");
    }

    [Fact]
    public void An_envelope_without_a_tenant_yields_no_tenant_roles()
    {
        var @event = Event(_user.ToString(), """{"roles":["authenticated"],"tenantRoles":["dispatcher"]}""") with { TenantId = null };

        EnvelopeReading.CallerOf(@event).Context.TenantRoles.ShouldBeEmpty();
        EventActor.Of(@event, RoleCatalog.Create(["dispatcher"]), Assignable("dispatcher")).TenantRoles.ShouldBeEmpty();
    }

    [Fact]
    public void A_built_in_named_as_a_tenant_role_is_dropped_not_thrown()
    {
        var @event = Event(_user.ToString(), """{"roles":["authenticated"],"tenantRoles":["admin","anon","authenticated","dispatcher"]}""");

        EnvelopeReading.CallerOf(@event).Context.TenantRoles.Select(role => role.Name).ShouldBe(["dispatcher"]);
        EventActor.Of(@event, RoleCatalog.Create(["dispatcher"]), Assignable("dispatcher")).TenantRoles.Select(role => role.Name).ShouldBe(["dispatcher"]);
    }

    [Fact]
    public void An_anonymous_change_carries_no_tenant_roles()
    {
        var @event = Event(authId: null, """{"roles":["anon"],"tenantRoles":["dispatcher"]}""");

        EnvelopeReading.CallerOf(@event).Context.TenantRoles.ShouldBeEmpty("a condition never sees tenant roles without an identity");
        EventActor.Of(@event, RoleCatalog.Create(["dispatcher"]), Assignable("dispatcher")).TenantRoles.ShouldBeEmpty();
    }

    /// <summary>D10a: a role the descriptor still declares but no longer lets a membership carry is not a tenant role any more.</summary>
    [Fact]
    public void A_recorded_tenant_role_no_longer_assignable_is_not_held()
    {
        var @event = Event(_user.ToString(), """{"roles":["authenticated"],"tenantRoles":["dispatcher","technician"]}""");
        var declared = RoleCatalog.Create(["dispatcher", "technician"]);

        EventActor.Of(@event, declared, Assignable("dispatcher", "technician")).TenantRoles.Select(role => role.Name)
            .Order(StringComparer.Ordinal).ShouldBe(["dispatcher", "technician"]);
        EventActor.Of(@event, declared, Assignable("technician")).TenantRoles.Select(role => role.Name).ShouldBe(["technician"]);
        EventActor.Of(@event, declared, SelfServicePolicy.None).TenantRoles.ShouldBeEmpty("without selfService no role is a tenant role");
    }

    /// <summary>A self-service policy whose <c>assignableRoles</c> are <paramref name="names"/>.</summary>
    private static SelfServicePolicy Assignable(params string[] names) =>
        new(true, names.Select(Role.Application).ToHashSet(), null, null, SelfServicePolicy.DefaultMaxPerAccount);

    private static AlvoEvent Event(string? authId, string? authClaims) => new()
    {
        Id = AlvoEventId.Create(DateTimeOffset.UtcNow),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.created",
        Time = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero),
        Subject = "tickets/1",
        PartitionKey = "tickets:1",
        AuthType = AlvoEventAuthType.User,
        AuthId = authId,
        AuthClaims = authClaims,
        TenantId = _tenant.ToString(),
        CorrelationId = "c",
        Data = new AlvoEventData { Record = AlvoRecord.Empty },
    };
}
