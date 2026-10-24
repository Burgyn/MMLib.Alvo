using System.Globalization;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The caller as at the event (spec 2026-10-14 E8): its id, tenant and the roles its envelope recorded, resolved against
/// the roles the descriptor still declares — a role that is no longer declared is not held.
/// </summary>
/// <remarks>
/// Built from the envelope alone, through <see cref="EnvelopeReading.CallerOf"/> — the one reading the subscription
/// conditions resolve against too — and never from whoever dispatches. An envelope with no readable credential is an
/// anonymous change: its actor holds only <see cref="Role.Anon"/>, whatever roles the envelope records beside it.
/// </remarks>
internal static class EventActor
{
    internal static TenantId? TenantOf(AlvoEvent @event) =>
        TenantId.TryParse(@event.TenantId, CultureInfo.InvariantCulture, out var tenant) ? tenant : null;

    internal static AlvoContext Of(AlvoEvent @event, RoleCatalog declared)
    {
        var recorded = EnvelopeReading.CallerOf(@event).Context;
        if (recorded.User == default)
        {
            return AlvoContext.Anonymous with { Tenant = recorded.Tenant };
        }

        return recorded with { Roles = StillDeclared(recorded.Roles, declared), ActorKind = KindOf(@event.AuthType) };
    }

    private static HashSet<Role> StillDeclared(IReadOnlySet<Role> recorded, RoleCatalog declared)
    {
        var roles = recorded
            .SelectMany(role => declared.TryGet(role.Name, out var held) ? new[] { held } : [])
            .ToHashSet();

        return roles.Count > 0 ? roles : [Role.Anon];
    }

    private static AlvoActorKind KindOf(string authType) => authType switch
    {
        AlvoEventAuthType.ApiKey => AlvoActorKind.ApiKey,
        AlvoEventAuthType.User => AlvoActorKind.User,
        AlvoEventAuthType.System => AlvoActorKind.System,
        AlvoEventAuthType.Unauthenticated => AlvoActorKind.Anonymous,
        _ => AlvoActorKind.Unknown,
    };
}
