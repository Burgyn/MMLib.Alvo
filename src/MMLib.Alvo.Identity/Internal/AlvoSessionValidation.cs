using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
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
/// and a disabled account. A store that throws is not a refusal and not a pass: the exception propagates
/// and the request fails, which is the only honest answer to "could not tell".
/// </para>
/// <para>
/// <b>It asks <see cref="IAlvoUserStore"/>, not the context resolver.</b> The resolver also refuses a
/// caller when no descriptor has been applied yet, which is a statement about the project rather than
/// the account — dropping every session to sign-in on an empty project would loop the one operator who
/// could apply the first descriptor. Whether the account may hold a session is the store's question.
/// </para>
/// </remarks>
internal static class AlvoSessionValidation
{
    /// <summary>Decides whether <paramref name="session"/> still names an enabled account.</summary>
    /// <param name="session">The principal the cookie or the circuit holds.</param>
    /// <param name="users">The membership store, read as of this call.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when the session may continue.</returns>
    internal static async ValueTask<bool> StillStandsAsync(
        ClaimsPrincipal session, IAlvoUserStore users, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(users);

        var subject = session.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!UserId.TryParse(subject, null, out var id) || id == default)
        {
            return false;
        }

        return await users.FindAsync(id, cancellationToken).ConfigureAwait(false) is { IsDisabled: false };
    }

    /// <summary>
    /// The cookie scheme's <c>OnValidatePrincipal</c>: re-reads the account on every request and
    /// rejects — and clears — a cookie whose account is gone or disabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every request, with no throttle, deliberately.</b> A throttle would have to remember when the
    /// cookie was last checked, which means re-issuing the cookie to carry the timestamp, and it would
    /// open exactly the window this exists to close. What it would save is small: the dashboard is a
    /// Blazor circuit, so after the first page load it makes almost no HTTP requests, and each check is
    /// one indexed read — the same one the context resolver already makes per management call.
    /// </para>
    /// <para>
    /// The store comes from the request's own scope, which is fresh per request, and
    /// <see cref="IAlvoUserStore"/>'s reads are untracked by contract.
    /// </para>
    /// </remarks>
    /// <param name="context">The cookie handler's validation context.</param>
    /// <returns>A task that completes when the decision has been applied.</returns>
    internal static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = context.HttpContext;
        var users = http.RequestServices.GetRequiredService<IAlvoUserStore>();
        if (context.Principal is { } session
            && await StillStandsAsync(session, users, http.RequestAborted).ConfigureAwait(false))
        {
            return;
        }

        context.RejectPrincipal();
        await http.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
    }
}
