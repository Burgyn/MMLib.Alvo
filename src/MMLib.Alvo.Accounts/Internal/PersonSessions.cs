using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>A session an access token names, its signature, claims and lifetime already checked.</summary>
/// <param name="Account">The account (<c>sub</c>).</param>
/// <param name="Session">The session (<c>sid</c>).</param>
/// <param name="Provider">The provider the session began with (<c>idp</c>).</param>
internal sealed record PersonSession(UserId Account, Guid Session, string Provider);

/// <summary>
/// Turns a presented access token into the session it names (spec §5.7 steps 1–2): validated in memory, then — for a caller
/// that is to act — confirmed live with one store read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two halves, so a caller can put work between them.</b> <see cref="IdentifyAsync"/> does no I/O on a known key; the
/// person resolver consults membership resolution after it and before <see cref="IsLiveAsync"/>, so a refused tenant
/// costs no read (S2 D13). <see cref="AuthenticateAsync"/> is both, for the routes that have no tenant step.
/// </para>
/// <para>
/// <b>The descriptor stays the switch</b> (S2 D10): a session whose provider the applied descriptor no longer lists is not
/// live, whatever the store says — checked in memory, before the read.
/// </para>
/// </remarks>
/// <param name="tokens">Validates the access token.</param>
/// <param name="store">Answers whether the session is live.</param>
/// <param name="signIn">The applied descriptor's providers and the audience's default.</param>
/// <param name="options">The accounts options: the issuer and the audience.</param>
/// <param name="clock">The clock the session's expiry is judged on.</param>
internal sealed class PersonSessions(
    AccessTokens tokens,
    IAccountStore store,
    DescriptorSignIn signIn,
    IOptions<AlvoAccountsOptions> options,
    TimeProvider clock)
{
    /// <summary>The live session <paramref name="accessToken"/> names, or <see langword="null"/>.</summary>
    /// <param name="accessToken">The bearer token presented.</param>
    /// <param name="request">The request, whose URL is the issuer in Development when none is configured.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<PersonSession?> AuthenticateAsync(string accessToken, HttpRequest request, CancellationToken ct)
        => await IdentifyAsync(accessToken, request, ct).ConfigureAwait(false) is { } session
            && await IsLiveAsync(session, ct).ConfigureAwait(false)
                ? session
                : null;

    /// <summary>
    /// The session <paramref name="accessToken"/> names when its signature, claims and lifetime hold — no store read, and no
    /// word on whether the session still lives.
    /// </summary>
    /// <param name="accessToken">The bearer token presented.</param>
    /// <param name="request">The request, whose URL is the issuer in Development when none is configured.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<PersonSession?> IdentifyAsync(string accessToken, HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        var settings = options.Value;
        var subject = await tokens.ValidateAsync(
            accessToken,
            AccountsIssuer.IssuerFor(request, settings),
            AccountsIssuer.AudienceFor(settings, signIn.Policy),
            ct).ConfigureAwait(false);
        return subject is null ? null : new PersonSession(subject.Account, subject.Session, subject.Provider);
    }

    /// <summary>
    /// Whether <paramref name="session"/>'s provider is still listed and the store holds it unrevoked, unexpired, and of an
    /// account not disabled — one read.
    /// </summary>
    /// <param name="session">A session <see cref="IdentifyAsync"/> answered.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<bool> IsLiveAsync(PersonSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        return signIn.Admits(signIn.Policy, session.Provider)
            && await store.IsSessionLiveAsync(session.Account, session.Session, clock.GetUtcNow(), ct).ConfigureAwait(false);
    }
}
