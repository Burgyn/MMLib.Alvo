using System.Text.Json.Serialization;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>One membership as its person sees it: in <c>GET {prefix}/user</c>'s <c>memberships</c> (spec §5.5).</summary>
/// <remarks>
/// Member names are fixed with <see cref="JsonPropertyNameAttribute"/>, so a host's naming policy cannot rename a member a
/// client reads — the <c>UserResponse</c> precedent; the body that carries it is written with its own serializer options.
/// </remarks>
/// <param name="TenantId">The tenant's id — the value of <c>X-Alvo-Tenant</c> that acts in it.</param>
/// <param name="Name">The tenant's name.</param>
/// <param name="Kind"><c>personal</c> or <c>shared</c>.</param>
/// <param name="Roles">The tenant roles the person holds there under the applied descriptor, in ordinal order.</param>
/// <param name="IsOwner">Whether the person manages the tenant; never a role (spec §6.2).</param>
internal sealed record MembershipResponse(
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("isOwner")] bool IsOwner)
{
    /// <summary>The <see cref="Kind"/> of a personal tenant.</summary>
    internal const string PersonalKind = "personal";

    /// <summary>The <see cref="Kind"/> of a shared tenant.</summary>
    internal const string SharedKind = "shared";

    /// <summary>The response for <paramref name="membership"/> with its effective <paramref name="roles"/>.</summary>
    /// <param name="membership">The stored membership.</param>
    /// <param name="roles">Its effective tenant roles (S3 plan D5).</param>
    internal static MembershipResponse From(StoredMembership membership, IEnumerable<Role> roles)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(roles);
        return new(
            membership.Tenant.Value,
            membership.Name,
            membership.Kind == TenantKind.Personal ? PersonalKind : SharedKind,
            [.. roles.Select(role => role.Name).Order(StringComparer.Ordinal)],
            membership.IsOwner);
    }
}
