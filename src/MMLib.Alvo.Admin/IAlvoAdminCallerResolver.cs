using MMLib.Alvo.Auth;
using System.Security.Claims;

namespace MMLib.Alvo.Admin;

/// <summary>
/// Turns the operator a host has signed in into the caller Alvo authorizes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a port rather than a direct call.</b> The dashboard knows a
/// <see cref="ClaimsPrincipal"/>: whoever the host's authentication produced. Alvo authorizes an
/// <see cref="AlvoPrincipal"/>: a user id, the roles the descriptor declares, and one tenant.
/// Between the two sits the identity store, and this package references neither it nor the core
/// (design §1.2) — so the conversion is a port the host fills, exactly as every other piece of
/// infrastructure in Alvo is.
/// </para>
/// <para>
/// <b>It is also the seam that keeps an embedded host honest.</b> A host with its own users and
/// its own roles implements this over its own store and the dashboard works unchanged; nothing in
/// the screens assumes ASP.NET Core Identity, because nothing in them can see it.
/// </para>
/// <para>
/// <b>A <see langword="null"/> answer is a refusal, not an anonymous caller.</b> An implementation
/// that cannot map the operator — no such user, disabled, or a tenant it may not confirm — returns
/// nothing, and the dashboard shows the refusal. Minting a caller with no tenant instead would
/// silently widen "you may not act there" into "you act everywhere unscoped".
/// </para>
/// </remarks>
public interface IAlvoAdminCallerResolver
{
    /// <summary>Resolves the signed-in operator's Alvo caller.</summary>
    /// <param name="signedIn">The principal the host's authentication produced.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The caller, or <see langword="null"/> when this operator is not one.</returns>
    ValueTask<AlvoPrincipal?> ResolveAsync(
        ClaimsPrincipal signedIn, CancellationToken cancellationToken);
}
