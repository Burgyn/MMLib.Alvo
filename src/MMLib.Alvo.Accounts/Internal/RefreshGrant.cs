using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The refresh grant (RFC 6749 §6, spec §5.5): rotation with reuse detection (RFC 9700 §4.14) and a grace window that never
/// forks (spec D6).
/// </summary>
/// <remarks>
/// <para>
/// <b>A value that is not genuine never reaches the store.</b> The refresh token's MAC is checked first, so a guessed or
/// mangled value costs no read; only a genuine id is presented to <see cref="IAccountStore.RefreshAsync"/>, which rotates
/// it, answers the child it already made within the grace window, or — after the window — revokes the session in the same
/// transaction before this answers.
/// </para>
/// <para>
/// <b>Every dead refresh token answers the same <c>invalid_grant</c></b>: unknown, mangled, idle, expired, revoked, reused,
/// a disabled account's, or a session whose provider the applied descriptor no longer lists (S2 D10). Telling them apart
/// would tell a thief which stolen token is still worth replaying.
/// </para>
/// </remarks>
/// <param name="store">Rotates the token, in one transaction.</param>
/// <param name="keys">Holds the refresh HMAC key.</param>
/// <param name="signIn">The applied descriptor's providers.</param>
/// <param name="issuance">Answers a granted request.</param>
/// <param name="options">The accounts options: the grace window and the idle limit.</param>
/// <param name="clock">The clock every boundary is judged on.</param>
internal sealed class RefreshGrant(
    IAccountStore store,
    AccountKeys keys,
    DescriptorSignIn signIn,
    TokenIssuance issuance,
    IOptions<AlvoAccountsOptions> options,
    TimeProvider clock)
{
    /// <summary>RFC 6749 §6's <c>grant_type</c>.</summary>
    internal const string GrantType = "refresh_token";

    private const string RefreshTokenParameter = "refresh_token";

    /// <summary>Rotates the refresh token <paramref name="form"/> carries.</summary>
    /// <param name="form">The token request's parameters.</param>
    /// <param name="request">The request, for the issuer in Development.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async Task<IResult> RefreshAsync(TokenForm form, HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form[RefreshTokenParameter] is not { } presented)
        {
            return TokenProblems.MissingRefreshToken();
        }

        if (!RefreshTokenFormat.TryParse(presented, await keys.RefreshMacKeyAsync(ct).ConfigureAwait(false), out var id))
        {
            return TokenProblems.InvalidGrant();
        }

        var outcome = await store.RefreshAsync(Presentation(id), ct).ConfigureAwait(false);
        var policy = signIn.Policy;
        return outcome is { Status: RefreshStatus.Rotated or RefreshStatus.Replayed, ChildId: { } child, Account: { } account, Session: { } session, Provider: { } provider }
            && signIn.Admits(policy, provider)
                ? await issuance.IssueAsync(new AccessTokenSubject(account, session, provider), child, policy, request, exchanged: false, ct).ConfigureAwait(false)
                : TokenProblems.InvalidGrant();
    }

    private RefreshRequest Presentation(Guid id)
    {
        var settings = options.Value;
        return new RefreshRequest(
            id,
            Guid.NewGuid(),
            clock.GetUtcNow(),
            TimeSpan.FromSeconds(settings.RefreshReuseGraceSeconds),
            TimeSpan.FromDays(settings.RefreshIdleDays),
            PersonalTenant: null);
    }
}
