using System.Text.Json.Serialization;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>One member of a tenant, as its other members see them (spec §6.3): never an email.</summary>
/// <remarks>
/// Member names are fixed with <see cref="JsonPropertyNameAttribute"/>, so a host's naming policy cannot rename a member a
/// client reads (the <see cref="MembershipResponse"/> precedent).
/// </remarks>
/// <param name="UserId">The member's account id — the id the member routes take.</param>
/// <param name="Name">The account's display name, or <see langword="null"/>.</param>
/// <param name="AvatarUrl">The account's avatar URL, or <see langword="null"/>.</param>
/// <param name="Roles">The tenant roles the member holds there under the applied descriptor, in ordinal order.</param>
/// <param name="IsOwner">Whether the member manages the tenant; never a role (spec §6.2).</param>
internal sealed record MemberResponse(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("avatarUrl")] string? AvatarUrl,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("isOwner")] bool IsOwner)
{
    /// <summary>The response for <paramref name="member"/> with its effective <paramref name="roles"/>.</summary>
    /// <param name="member">The member as the registry lists it.</param>
    /// <param name="roles">Its effective tenant roles (S3 plan D5) — never its stored names.</param>
    internal static MemberResponse From(MemberView member, IEnumerable<Role> roles)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(roles);
        return new(
            member.User.Value,
            member.DisplayName,
            member.AvatarUrl,
            [.. roles.Select(role => role.Name).Order(StringComparer.Ordinal)],
            member.IsOwner);
    }
}
