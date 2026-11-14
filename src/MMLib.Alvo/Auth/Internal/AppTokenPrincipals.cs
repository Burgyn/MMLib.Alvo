using MMLib.Alvo.Rules;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// The principal a usable app token acts as (S5 plan D5, D6, spec §7.1) — or <see langword="null"/>, the refusal of the
/// whole principal. A <see cref="ApiKeyKind.Configured"/> key never reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-deny on the applied descriptor</b> (D11): a kind <c>auth.accessTokens</c> does not turn on is refused, and a
/// service token also needs <c>tenancy.selfService</c> on — so turning a kind off stops every token of it at the next
/// request, and turning it back on revives the unexpired ones.
/// </para>
/// <para>
/// <b>A personal token acts as its person</b>, <see cref="AlvoActorKind.User"/>, with the tenant roles of their
/// <em>current</em> membership — read live through <see cref="IMembershipResolution"/> on every request, never cached, so a
/// role change or a removal counts at the next request exactly as it does for the person's session. A tenantless one acts
/// in no tenant and reads no membership: it never falls back to the personal tenant it was not minted for.
/// </para>
/// <para>
/// <b>A service token acts as itself</b>, <see cref="AlvoActorKind.ApiKey"/>, with its stored roles ∩ the applied
/// <c>assignableRoles</c> — no membership read: the token's own row dies with its minter's membership (D10).
/// </para>
/// <para>Either kind holds <c>authenticated</c> and no project role: what it may do in a tenant is its tenant roles.</para>
/// </remarks>
/// <param name="memberships">Membership resolution, read for a personal token with a tenant.</param>
/// <param name="catalogs">The applied policy catalog.</param>
internal sealed class AppTokenPrincipals(IMembershipResolution memberships, IPolicyCatalogProvider catalogs)
{
    private static readonly IReadOnlySet<Role> _roles = new HashSet<Role> { Role.Authenticated };

    /// <summary>The principal <paramref name="token"/> acts as in <paramref name="tenant"/>, or <see langword="null"/>.</summary>
    /// <param name="token">A usable app token, its secret verified.</param>
    /// <param name="tenant">The tenant <see cref="TenantResolver"/> resolved for it.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<AlvoPrincipal?> ResolveAsync(ApiKeyRecord token, TenantId? tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        var catalog = catalogs.Current;
        var selfService = catalog?.SelfService ?? SelfServicePolicy.None;
        if (!(catalog?.AppTokens ?? AppTokenPolicy.None).Allows(token.Kind))
        {
            return null;
        }

        return token.Kind switch
        {
            ApiKeyKind.Personal => await PersonalAsync(token, tenant, ct).ConfigureAwait(false),
            ApiKeyKind.Service when selfService.Enabled && tenant is not null =>
                Principal(token, tenant, selfService.Effective(token.RoleNames), AlvoActorKind.ApiKey),
            _ => null,
        };
    }

    private async ValueTask<AlvoPrincipal?> PersonalAsync(ApiKeyRecord token, TenantId? tenant, CancellationToken ct)
    {
        if (tenant is null)
        {
            return Principal(token, null, MembershipResolution.NoTenant.TenantRoles, AlvoActorKind.User);
        }

        var membership = await memberships.ResolveAsync(token.User, tenant, ct).ConfigureAwait(false);
        return membership.Refused || membership.Tenant != tenant
            ? null
            : Principal(token, tenant, membership.TenantRoles, AlvoActorKind.User);
    }

    private static AlvoPrincipal Principal(
        ApiKeyRecord token, TenantId? tenant, IReadOnlySet<Role> tenantRoles, AlvoActorKind actorKind) => new()
        {
            Context = new AlvoContext
            {
                User = token.User,
                Roles = _roles,
                Tenant = tenant,
                TenantRoles = tenantRoles,
                ActorKind = actorKind,
            },
            Scopes = token.Scopes,
            KeyId = token.KeyId,
        };
}
