using MMLib.Alvo.Tenants.SelfService;

using System.Globalization;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The caller as at the event (spec 2026-10-14 E8): its id, tenant and the roles and tenant roles its envelope recorded,
/// resolved against the applied descriptor — a role that is no longer declared is not held, and a tenant role that is no
/// longer in <c>tenancy.selfService.assignableRoles</c> is not held either (S3 plan D10a).
/// </summary>
/// <remarks>
/// <para>
/// Built from the envelope alone, through <see cref="EnvelopeReading.CallerOf"/> — the one reading the subscription
/// conditions resolve against too, through the same <see cref="AssignableTenantRoles"/> — and never from whoever dispatches.
/// An envelope with no readable credential is an anonymous change: its actor holds only <see cref="Role.Anon"/> and no
/// tenant role, whatever the envelope records beside it. Unlike the roles, which fall back to <see cref="Role.Anon"/>, no
/// tenant role stands in for none.
/// </para>
/// <para>
/// <b>Assignable, not merely declared.</b> A tenant role is a membership's role; a declared role the descriptor no longer
/// lets a membership carry is a project role at most, and must not reach a scoped rule through an old envelope. A delivery
/// that runs as the actor narrows the result further, to the membership the person still holds (<c>DeliveryRunner</c>, D10b).
/// </para>
/// </remarks>
internal static class EventActor
{
    internal static TenantId? TenantOf(AlvoEvent @event) =>
        TenantId.TryParse(@event.TenantId, CultureInfo.InvariantCulture, out var tenant) ? tenant : null;

    /// <summary>The actor of <paramref name="event"/>, resolved against the applied descriptor's roles and tenant roles.</summary>
    /// <param name="event">The committed event.</param>
    /// <param name="declared">The roles the applied descriptor declares.</param>
    /// <param name="selfService">The applied self-service policy, whose <c>assignableRoles</c> are the only tenant roles.</param>
    internal static AlvoContext Of(AlvoEvent @event, RoleCatalog declared, SelfServicePolicy selfService)
    {
        var recorded = EnvelopeReading.CallerOf(@event).Context;
        if (recorded.User == default)
        {
            return AlvoContext.Anonymous with { Tenant = recorded.Tenant };
        }

        return recorded with
        {
            Roles = StillDeclaredOrAnon(recorded.Roles, declared),
            TenantRoles = AssignableTenantRoles(recorded.TenantRoles, declared, selfService),
            ActorKind = KindOf(@event.AuthType),
        };
    }

    /// <summary>
    /// The recorded tenant roles still held under the applied descriptor: recorded ∩ declared ∩ <c>assignableRoles</c> — none
    /// at all without <c>tenancy.selfService</c>.
    /// </summary>
    /// <param name="recorded">The tenant roles the envelope recorded.</param>
    /// <param name="declared">The roles the applied descriptor declares.</param>
    /// <param name="selfService">The applied self-service policy.</param>
    internal static IReadOnlySet<Role> AssignableTenantRoles(IReadOnlySet<Role> recorded, RoleCatalog declared, SelfServicePolicy selfService)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(selfService);

        return recorded.Count == 0 ? recorded : selfService.Effective(StillDeclared(recorded, declared).Select(role => role.Name));
    }

    private static HashSet<Role> StillDeclaredOrAnon(IReadOnlySet<Role> recorded, RoleCatalog declared)
    {
        var roles = StillDeclared(recorded, declared);
        return roles.Count > 0 ? roles : [Role.Anon];
    }

    private static HashSet<Role> StillDeclared(IReadOnlySet<Role> recorded, RoleCatalog declared) => recorded
        .SelectMany(role => declared.TryGet(role.Name, out var held) ? new[] { held } : [])
        .ToHashSet();

    private static AlvoActorKind KindOf(string authType) => authType switch
    {
        AlvoEventAuthType.ApiKey => AlvoActorKind.ApiKey,
        AlvoEventAuthType.User => AlvoActorKind.User,
        AlvoEventAuthType.System => AlvoActorKind.System,
        AlvoEventAuthType.Unauthenticated => AlvoActorKind.Anonymous,
        _ => AlvoActorKind.Unknown,
    };
}
