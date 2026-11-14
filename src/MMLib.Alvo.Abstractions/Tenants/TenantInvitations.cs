namespace MMLib.Alvo.Tenants;

/// <summary>
/// One invitation's creation, with every id and instant chosen by the caller (accounts spec §6.1, S4 plan D2, D9). The
/// store never sees the code itself — only <paramref name="CodeHash"/>, the lowercase hex SHA-256 of it.
/// </summary>
/// <param name="Id">The new invitation's id.</param>
/// <param name="Tenant">The shared tenant it invites into.</param>
/// <param name="Actor">The owner creating it, stored as its creator.</param>
/// <param name="CodeHash">The SHA-256 of the code, lowercase hex; unique across every invitation.</param>
/// <param name="Roles">The tenant roles an accepted membership starts with, verbatim.</param>
/// <param name="GrantsOwner">Whether an accepted membership is an owner's.</param>
/// <param name="Email">The trusted email the invitation is bound to, or <see langword="null"/> for anyone.</param>
/// <param name="MaxUses">How many people may accept it.</param>
/// <param name="ExpiresAt">The instant from which it no longer admits anyone.</param>
/// <param name="Now">The instant of the creation.</param>
internal sealed record CreateInvitationRequest(
    Guid Id, TenantId Tenant, UserId Actor, string CodeHash, IReadOnlyList<string> Roles, bool GrantsOwner,
    string? Email, int MaxUses, DateTimeOffset ExpiresAt, DateTimeOffset Now);

/// <summary>A creation's status and, when it created the invitation, the invitation as stored.</summary>
/// <param name="Status">What the creation did.</param>
/// <param name="Invitation">The stored invitation, for <see cref="MemberChangeStatus.Changed"/>.</param>
internal readonly record struct CreateInvitationOutcome(MemberChangeStatus Status, InvitationView? Invitation);

/// <summary>One invitation as its tenant's owners see it — never its code, which exists only in the creation's answer.</summary>
/// <param name="Id">The invitation.</param>
/// <param name="Tenant">The tenant it invites into.</param>
/// <param name="Roles">The stored tenant-role names, verbatim.</param>
/// <param name="GrantsOwner">Whether an accepted membership is an owner's.</param>
/// <param name="Email">The email it is bound to, if any.</param>
/// <param name="MaxUses">How many people may accept it.</param>
/// <param name="Uses">How many have.</param>
/// <param name="ExpiresAt">The instant from which it no longer admits anyone.</param>
/// <param name="CreatedBy">The owner who created it.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="RevokedAt">When it was first revoked, or <see langword="null"/>.</param>
internal sealed record InvitationView(
    Guid Id, TenantId Tenant, IReadOnlyList<string> Roles, bool GrantsOwner, string? Email, int MaxUses, int Uses,
    DateTimeOffset ExpiresAt, UserId CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>An owner's revocation of one invitation of a tenant.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Actor">The owner revoking.</param>
/// <param name="Invitation">The invitation.</param>
/// <param name="Now">The instant of the revocation.</param>
internal sealed record InvitationRevocation(TenantId Tenant, UserId Actor, Guid Invitation, DateTimeOffset Now);
