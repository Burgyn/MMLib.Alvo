using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// Adds the tenant claim to a signed-in operator's principal.
/// </summary>
/// <remarks>
/// <para>
/// The principal already carries the subject and the role claims — Identity's own factory does
/// that once <c>AddRoles</c> is registered. What it does not know about is the tenant, which is
/// Alvo's column rather than Identity's.
/// </para>
/// <para>
/// <b>The claim is minted here, at sign-in, and is not the authority.</b>
/// <see cref="AlvoIdentityContextResolver"/> reads the column when it mints an
/// <see cref="AlvoContext"/>, so an administrator who revokes a grant has revoked it for the next
/// request rather than for the next sign-in. The claim exists so the dashboard can <em>say</em>
/// which tenant an operator holds without a database round trip per render — a display fact, never
/// an authorization one.
/// </para>
/// </remarks>
internal sealed class AlvoIdentityClaimsFactory(
    UserManager<AlvoIdentityUser> users,
    RoleManager<AlvoIdentityRole> roles,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AlvoIdentityUser, AlvoIdentityRole>(users, roles, options)
{
    /// <summary>The claim type the dashboard reads the tenant off.</summary>
    /// <remarks>
    /// Spelled as a literal rather than referenced from <c>MMLib.Alvo.Admin</c>: this package does
    /// not depend on the dashboard, and it is the dashboard that is optional. A test pins the two
    /// spellings together, which is the cheaper direction of the same guarantee.
    /// </remarks>
    public const string TenantClaimType = "alvo:tenant";

    /// <inheritdoc/>
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AlvoIdentityUser user)
    {
        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

        if (user.TenantId is { } tenant)
        {
            identity.AddClaim(new Claim(TenantClaimType, tenant.ToString()));
        }

        return identity;
    }
}
