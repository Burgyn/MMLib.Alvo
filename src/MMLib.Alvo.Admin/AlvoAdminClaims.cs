namespace MMLib.Alvo.Admin;

/// <summary>
/// The claim types the dashboard reads off a signed-in operator.
/// </summary>
/// <remarks>
/// <para>
/// The host mints these when it signs somebody in, and the dashboard reads them. Spelled here so
/// the two halves cannot drift — the same argument <see cref="AlvoAdminAssets"/> makes about a
/// path, applied to a claim type, and for the same reason: a mismatched claim type is a string
/// that compiles on both sides and produces a screen that quietly shows "no tenant" to somebody
/// who has one.
/// </para>
/// <para>
/// There is no role claim type here because there is nothing to choose: roles are
/// <see cref="System.Security.Claims.ClaimTypes.Role"/>, which is what
/// <c>ClaimsPrincipal.IsInRole</c> reads and what every ASP.NET Core authorization primitive
/// assumes.
/// </para>
/// </remarks>
public static class AlvoAdminClaims
{
    /// <summary>The operator's tenant, as the canonical <c>Guid</c> string, when they hold one.</summary>
    /// <remarks>
    /// An operator holds one tenant, not a set (§2.7) — cross-tenant capability is a deliberate,
    /// audited grant deferred to #42, so a second value of this claim is a bug rather than a
    /// broader operator.
    /// </remarks>
    public const string Tenant = "alvo:tenant";
}
