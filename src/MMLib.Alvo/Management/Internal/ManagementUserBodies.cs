namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The request bodies the user-administration routes bind.
/// </summary>
/// <remarks>
/// One record per route rather than one shared shape with nullable members: a body whose fields
/// are all optional cannot distinguish <i>"do not change the tenant"</i> from <i>"remove the
/// tenant"</i>, and that distinction is the whole of <c>SetTenantAsync</c>.
/// </remarks>
/// <param name="RoleNames">The roles the person should have after the write.</param>
internal sealed record ManagementRoleAssignment(IReadOnlyList<string> RoleNames);

/// <summary>The tenant a person acts in.</summary>
/// <param name="Tenant">The tenant, or <see langword="null"/> to remove the grant.</param>
internal sealed record ManagementTenantGrant(Guid? Tenant);

/// <summary>Whether a person, or an end user's account, is barred from signing in.</summary>
/// <remarks>
/// Nullable, so a body without the member is refused rather than read as "enable": on a ban, the absent answer must not
/// be the permissive one. One record for both disable routes (#526), so the two cannot drift apart again.
/// </remarks>
/// <param name="Disabled">Whether they are to be barred, or <see langword="null"/> when the body did not say.</param>
internal sealed record ManagementDisabledFlag(bool? Disabled);

/// <summary>An operator's grant of a tenant's ownership to an end user's account (#543).</summary>
/// <param name="Roles">The tenant roles the owner holds, each in <c>tenancy.selfService.assignableRoles</c>, or <see langword="null"/> to keep a member's roles.</param>
internal sealed record ManagementOwnerGrant(IReadOnlyList<string>? Roles);

/// <summary>What disabling or enabling an end user's account answers (#543).</summary>
/// <param name="OrphanedTenants">The shared tenants the account owns that no enabled owner can manage after the change.</param>
internal sealed record ManagementAccountDisabled(IReadOnlyList<ManagementOrphanedTenant> OrphanedTenants);

/// <summary>A shared tenant no enabled owner can manage.</summary>
/// <param name="Id">The tenant's id.</param>
/// <param name="Name">The tenant's name.</param>
internal sealed record ManagementOrphanedTenant(Guid Id, string Name);
