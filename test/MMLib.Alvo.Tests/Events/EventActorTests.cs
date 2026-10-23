using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

namespace MMLib.Alvo.Tests.Events;

/// <summary>The caller as at the event (spec 2026-10-14 E8), rebuilt from the envelope for <c>AlvoRunAs.EventActor</c>.</summary>
public sealed class EventActorTests
{
    private static readonly Guid _user = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _tenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void The_actor_is_the_recorded_caller_with_the_roles_it_still_holds_by_declaration()
    {
        var actor = EventActor.Of(Event(_user.ToString(), "{\"roles\":[\"agent\",\"authenticated\",\"retired-role\"]}"), RoleCatalog.Create(["agent"]));

        actor.User.Value.ShouldBe(_user);
        actor.Tenant.ShouldBe(new TenantId(_tenant));
        actor.Roles.Select(role => role.Name).Order(StringComparer.Ordinal).ShouldBe(["agent", "authenticated"]);
        actor.ActorKind.ShouldBe(AlvoActorKind.User);
    }

    [Fact]
    public void An_actor_without_a_readable_role_holds_only_anon() =>
        EventActor.Of(Event(_user.ToString(), authClaims: null), RoleCatalog.BuiltInOnly).Roles.ShouldBe([Role.Anon]);

    [Fact]
    public void An_actor_whose_every_recorded_role_is_no_longer_declared_holds_only_anon() =>
        EventActor.Of(Event(_user.ToString(), "{\"roles\":[\"retired-role\"]}"), RoleCatalog.BuiltInOnly).Roles.ShouldBe([Role.Anon]);

    [Fact]
    public void An_anonymous_change_has_an_anonymous_actor_in_its_tenant()
    {
        var actor = EventActor.Of(Event(authId: null, authClaims: null), RoleCatalog.BuiltInOnly);

        actor.User.ShouldBe(default);
        actor.Tenant.ShouldBe(new TenantId(_tenant));
    }

    [Fact]
    public void An_envelope_without_a_credential_never_lends_its_recorded_roles_to_an_anonymous_actor() =>
        EventActor.Of(Event(authId: null, "{\"roles\":[\"admin\"]}"), RoleCatalog.BuiltInOnly).Roles.ShouldBe([Role.Anon]);

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
