using System.Text.Json.Serialization;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// What a live invitation offers, as anyone holding its code sees it (spec §6.3, S4 plan D8, D11): the tenant, who invited,
/// the roles and ownership accepting it grants, until when — and whether it is bound to an email, never the email itself.
/// </summary>
/// <remarks>
/// <b>The roles are the effective ones</b> (S3 plan D5), the stored names the applied <c>assignableRoles</c> still lists.
/// Member names are fixed with <see cref="JsonPropertyNameAttribute"/> (the <see cref="MembershipResponse"/> precedent).
/// </remarks>
/// <param name="TenantName">The tenant's name.</param>
/// <param name="InviterName">The creator's display name, or <see langword="null"/>.</param>
/// <param name="Roles">The tenant roles accepting it grants, in ordinal order.</param>
/// <param name="IsOwner">Whether accepting it makes an owner.</param>
/// <param name="ExpiresAt">The instant from which it admits nobody.</param>
/// <param name="EmailBound">Whether only one trusted email may accept it.</param>
internal sealed record InvitationPreviewResponse(
    [property: JsonPropertyName("tenantName")] string TenantName,
    [property: JsonPropertyName("inviterName")] string? InviterName,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("isOwner")] bool IsOwner,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("emailBound")] bool EmailBound)
{
    /// <summary>The response for <paramref name="preview"/> under <paramref name="policy"/>.</summary>
    /// <param name="preview">The live invitation, as the registry previews it.</param>
    /// <param name="policy">The applied self-service policy, for the effective roles.</param>
    internal static InvitationPreviewResponse From(InvitationPreview preview, SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(policy);
        return new(
            preview.TenantName,
            preview.InviterName,
            [.. policy.Effective(preview.Roles).Select(role => role.Name).Order(StringComparer.Ordinal)],
            preview.GrantsOwner,
            preview.ExpiresAt,
            preview.EmailBound);
    }
}
