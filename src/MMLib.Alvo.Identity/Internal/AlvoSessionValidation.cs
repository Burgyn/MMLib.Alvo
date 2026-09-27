using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// Whether a signed-in session still names somebody who may hold one: the one decision the cookie
/// and the dashboard's circuit both re-ask.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a session is re-asked at all.</b> The cookie is an eight-hour sliding ticket minted at sign-in.
/// Nothing that happens to the account afterwards reaches it on its own, so a disabled operator kept
/// passing <c>[Authorize]</c> — the data behind every screen was refused per call, but the shell kept
/// navigating, and an operator who can still see the chrome has not been told they are out.
/// </para>
/// <para>
/// <b>Default-deny, and every refusal is <see langword="false"/>:</b> no subject claim, a subject that is
/// not a usable <see cref="UserId"/> (the all-zero value included), a subject the store does not hold,
/// a disabled account, and a security stamp claim that is missing or is not the stored one. A store that throws is not a refusal and not a pass: the exception propagates
/// and the request fails, which is the only honest answer to "could not tell".
/// </para>
/// <para>
/// <b>So an identity-store outage fails closed, and loudly.</b> While the store is unreachable, every
/// request that carries the cookie to an endpoint that guards something answers <c>500</c> — the dashboard's
/// pages, the Blazor hub, and anything else that is not a static asset or explicitly anonymous. The sign-in
/// page and static assets are not checked, and stay up. That is the default-deny reading of
/// "cannot tell whether this account is still enabled"; a request with no cookie is not checked and
/// reaches the sign-in page, whose own sign-in then fails on the same outage.
/// </para>
/// <para>
/// <b>It asks <see cref="IAlvoUserStore"/>, not the context resolver.</b> The resolver also refuses a
/// caller when no descriptor has been applied yet, which is a statement about the project rather than
/// the account — dropping every session to sign-in on an empty project would loop the one operator who
/// could apply the first descriptor. Whether the account may hold a session is the store's question.
/// </para>
/// <para>
/// <b>The security stamp ends every session on a credential change.</b> Setting a password rotates the stamp,
/// and a principal whose stamp claim no longer equals the stored one was minted before it — baas-analyza
/// §2.2's "password change → old sessions dead". It is ASP.NET Core Identity's <c>SecurityStampValidator</c>
/// check without its thirty-minute throttle, for the reason <see cref="ValidateCookieAsync"/> gives. A missing
/// claim is a refusal: every cookie <c>UserClaimsPrincipalFactory</c> mints carries one, because the EF store
/// supports stamps. Role, tenant and disable writes do not rotate the stamp in <c>UserManager</c> (only
/// password, email, user name, login and two-factor writes do), so this check ends sessions on a credential
/// change and on nothing an administrator does to access — the disable has its own condition above.
/// </para>
/// <para>
/// <b>The stamp is read as an untracked scalar by primary key</b>, straight from the identity store rather
/// than through <c>UserManager</c>: a tracked read on a scope that outlives it is exactly what the
/// administration's unit of work exists to avoid, and <see cref="IAlvoUserStore"/>, being a port every host
/// implements, has no notion of a stamp.
/// </para>
/// </remarks>
internal static class AlvoSessionValidation
{
    /// <summary>
    /// Decides whether <paramref name="session"/> still names an enabled account whose credential has not
    /// changed since the session was minted.
    /// </summary>
    /// <param name="session">The principal the cookie or the circuit holds.</param>
    /// <param name="scope">The scope to read the membership store and the identity store from, as of this call.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns><see langword="true"/> when the session may continue.</returns>
    internal static async ValueTask<bool> StillStandsAsync(
        ClaimsPrincipal session, IServiceProvider scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scope);

        var subject = session.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!UserId.TryParse(subject, null, out var id) || id == default)
        {
            return false;
        }

        var users = scope.GetRequiredService<IAlvoUserStore>();
        if (await users.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { IsDisabled: false })
        {
            return false;
        }

        return await StampStandsAsync(session, id, scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the session's security stamp claim is the one the store holds now.</summary>
    /// <param name="session">The principal the cookie or the circuit holds.</param>
    /// <param name="id">The principal's subject, already parsed.</param>
    /// <param name="scope">The scope the identity store and Identity's options are read from.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when the claim is present and equals the stored stamp.</returns>
    private static async ValueTask<bool> StampStandsAsync(
        ClaimsPrincipal session, UserId id, IServiceProvider scope, CancellationToken cancellationToken)
    {
        var claimType = scope.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        if (session.FindFirstValue(claimType) is not { Length: > 0 } presented)
        {
            return false;
        }

        var stored = await scope.GetRequiredService<AlvoIdentityDbContext>().Users.AsNoTracking()
            .Where(row => row.Id == id.Value)
            .Select(row => row.SecurityStamp)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return string.Equals(presented, stored, StringComparison.Ordinal);
    }

    /// <summary>Whether the request's endpoint must have its cookie re-checked.</summary>
    /// <remarks>
    /// <para>
    /// <b>An allow-list of two, and everything else is checked.</b> The check is skipped only for an endpoint
    /// that demonstrably guards nothing: a static asset (the <see cref="StaticAssetDescriptor"/>
    /// <c>MapStaticAssets</c> puts on each asset's endpoint), and an endpoint explicitly marked
    /// <see cref="IAllowAnonymous"/> — the sign-in page and its two form posts. Anything else is checked,
    /// fail-closed: an endpoint with no authorization metadata (it can still authorize imperatively, through an
    /// <c>AuthorizeView</c> or <c>User.IsInRole</c>), every kind of requirement the authorization middleware
    /// reads, whatever fallback a policy provider supplies, and a request no endpoint matched.
    /// </para>
    /// <para>
    /// <b>Why an allow-list rather than "whatever declares a requirement".</b> Mirroring the middleware means
    /// tracking three metadata kinds and an async fallback, and a host that authorizes imperatively declares
    /// none of them: a skip inferred from missing metadata hands a disabled operator's principal, role claims
    /// and all, to exactly that host. What must be skipped is known and small, so it is named.
    /// </para>
    /// <para>
    /// <b>A SignalR hub is checked even if marked anonymous</b>, because the Blazor circuit's connection is where
    /// the cookie's principal becomes the circuit's authentication state.
    /// </para>
    /// <para>
    /// <b>Why skip at all.</b> A first page load sends every stylesheet, script and font at once with the
    /// cookie, and each check is three store reads; and a store blip must not turn the sign-in page — the one
    /// screen that works with no session — into a <c>500</c>. A disabled operator loading a stylesheet reaches
    /// nothing, and their next request that does reach something is refused.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">The endpoint routing selected, or <see langword="null"/> when none matched.</param>
    /// <returns><see langword="true"/> when the cookie must be re-checked.</returns>
    internal static bool Guards(Endpoint? endpoint)
    {
        if (endpoint is null || endpoint.Metadata.GetMetadata<HubMetadata>() is not null)
        {
            return true;
        }

        return endpoint.Metadata.GetMetadata<StaticAssetDescriptor>() is null
            && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;
    }

    /// <summary>
    /// The cookie scheme's <c>OnValidatePrincipal</c>: re-reads the account on every request that guards
    /// something, and rejects — and clears — a cookie whose account is gone or disabled, or whose password
    /// was set after the cookie was minted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every request that reaches something, with no throttle, deliberately</b> — <see cref="Guards"/> says
    /// which requests those are. The alternative is ASP.NET Core Identity's
    /// <c>SecurityStampValidator</c> shape: skip the check while the cookie's <c>IssuedUtc</c> is younger
    /// than an interval, and renew the cookie on a successful check past it — which sliding expiration
    /// already does, so the throttle itself is cheap. What it costs is latency: a disabled operator's cookie
    /// would keep passing for up to one interval, the same lag the circuit's thirty seconds accepts. On HTTP
    /// there is no reason to accept it, because the saving is small — after the first page load the
    /// dashboard is a Blazor circuit and makes almost no HTTP requests. Each check is three indexed reads
    /// (the user row, then its role memberships, because <see cref="IAlvoUserStore.FindAsync"/> projects
    /// the roles, then the security stamp by primary key). Authentication runs ahead of the static-asset endpoints, so without <see cref="Guards"/>
    /// it would also run for every stylesheet, script and font of a first page load.
    /// </para>
    /// <para>
    /// The stores come from the request's own scope, which is fresh per request;
    /// <see cref="IAlvoUserStore"/>'s reads are untracked by contract, and the stamp read is untracked here.
    /// </para>
    /// </remarks>
    /// <param name="context">The cookie handler's validation context.</param>
    /// <returns>A task that completes when the decision has been applied.</returns>
    internal static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = context.HttpContext;
        if (!Guards(http.GetEndpoint()))
        {
            return;
        }

        if (context.Principal is { } session
            && await StillStandsAsync(session, http.RequestServices, http.RequestAborted).ConfigureAwait(false))
        {
            return;
        }

        context.RejectPrincipal();
        await http.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
    }
}
