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

/// <summary>Whether a person is barred from signing in.</summary>
/// <param name="Disabled">Whether they are barred.</param>
internal sealed record ManagementDisabledFlag(bool Disabled);

/// <summary>Whether an end user's account is disabled (accounts spec §5.6).</summary>
/// <remarks>
/// Nullable, unlike <see cref="ManagementDisabledFlag"/>, so a body without the member is refused rather than read as
/// "enable": on a ban, the absent answer must not be the permissive one.
/// </remarks>
/// <param name="Disabled">Whether the account is to be disabled, or <see langword="null"/> when the body did not say.</param>
internal sealed record ManagementAccountDisabledFlag(bool? Disabled);
