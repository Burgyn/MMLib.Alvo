using System.Text;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// One invitation as its tenant's owners see it (S4 plan D11): in <c>GET {prefix}/tenants/{id}/invitations</c>, and — the
/// one answer that carries <see cref="Code"/> — in the creation's 201.
/// </summary>
/// <remarks>
/// <para>
/// <b>The code is shown once.</b> Only the creation answer sets it; a listed invitation omits the member, since the store
/// holds only its hash. <see cref="PrintMembers"/> masks it, so neither a log line nor a debugger string carries it.
/// </para>
/// <para>
/// <b>The roles are the effective ones</b> (S3 plan D5): the stored names the applied <c>assignableRoles</c> still lists,
/// which is what accepting it would grant now. Member names are fixed with <see cref="JsonPropertyNameAttribute"/> (the
/// <see cref="MembershipResponse"/> precedent).
/// </para>
/// </remarks>
/// <param name="Id">The invitation's id — the one revoking it takes.</param>
/// <param name="Code">The code, on the creation's answer only; otherwise <see langword="null"/> and omitted.</param>
/// <param name="Roles">The tenant roles accepting it grants, in ordinal order.</param>
/// <param name="IsOwner">Whether accepting it makes an owner.</param>
/// <param name="Email">The email it is bound to, or <see langword="null"/>.</param>
/// <param name="MaxUses">How many people may accept it.</param>
/// <param name="Uses">How many have.</param>
/// <param name="ExpiresAt">The instant from which it admits nobody.</param>
/// <param name="CreatedBy">The owner who created it.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="RevokedAt">When it was revoked, or <see langword="null"/>.</param>
/// <param name="Status">One of <see cref="Revoked"/>, <see cref="UsedUp"/>, <see cref="Expired"/>, <see cref="Active"/>.</param>
internal sealed record InvitationResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("isOwner")] bool IsOwner,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("maxUses")] int MaxUses,
    [property: JsonPropertyName("uses")] int Uses,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("createdBy")] Guid CreatedBy,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("revokedAt")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("status")] string Status)
{
    /// <summary>The <see cref="Status"/> of an invitation that still admits someone.</summary>
    internal const string Active = "active";

    /// <summary>The <see cref="Status"/> of an invitation past its <see cref="ExpiresAt"/>.</summary>
    internal const string Expired = "expired";

    /// <summary>The <see cref="Status"/> of an invitation accepted <see cref="MaxUses"/> times.</summary>
    internal const string UsedUp = "usedUp";

    /// <summary>The <see cref="Status"/> of a revoked invitation.</summary>
    internal const string Revoked = "revoked";

    /// <summary>The response for <paramref name="invitation"/> at <paramref name="now"/>, carrying <paramref name="code"/> if given.</summary>
    /// <param name="invitation">The invitation as stored.</param>
    /// <param name="policy">The applied self-service policy, for the effective roles.</param>
    /// <param name="now">The instant its status is judged at.</param>
    /// <param name="code">The code, on the creation's answer only.</param>
    internal static InvitationResponse From(InvitationView invitation, SelfServicePolicy policy, DateTimeOffset now, string? code = null)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentNullException.ThrowIfNull(policy);
        return new(
            invitation.Id,
            code,
            [.. policy.Effective(invitation.Roles).Select(role => role.Name).Order(StringComparer.Ordinal)],
            invitation.GrantsOwner,
            invitation.Email,
            invitation.MaxUses,
            invitation.Uses,
            invitation.ExpiresAt,
            invitation.CreatedBy.Value,
            invitation.CreatedAt,
            invitation.RevokedAt,
            StatusOf(invitation, now));
    }

    /// <summary>The invitation's status at <paramref name="now"/>: revoked, then used up, then expired, else active.</summary>
    /// <param name="invitation">The invitation as stored.</param>
    /// <param name="now">The instant it is judged at.</param>
    internal static string StatusOf(InvitationView invitation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return invitation switch
        {
            { RevokedAt: not null } => Revoked,
            _ when invitation.Uses >= invitation.MaxUses => UsedUp,
            _ when invitation.ExpiresAt <= now => Expired,
            _ => Active,
        };
    }

    /// <summary>Prints every member, the code masked as <see cref="InvitationCode"/> prints itself.</summary>
    /// <param name="builder">The builder the record's <c>ToString</c> writes into.</param>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id)
            .Append(", Code = ").Append(Code is null ? string.Empty : InvitationCode.Prefix + "***")
            .Append(", Roles = [").AppendJoin(", ", Roles).Append(']')
            .Append(", IsOwner = ").Append(IsOwner)
            .Append(", MaxUses = ").Append(MaxUses)
            .Append(", Uses = ").Append(Uses)
            .Append(", Status = ").Append(Status);
        return true;
    }
}
