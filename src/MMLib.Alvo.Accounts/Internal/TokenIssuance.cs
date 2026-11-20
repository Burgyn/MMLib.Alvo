using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Answers a granted token request: a fresh access token for the session, and the value of the refresh token the store
/// made current — the same shape for an exchange and a refresh.
/// </summary>
/// <param name="tokens">Mints the access token.</param>
/// <param name="keys">Holds the refresh HMAC key the refresh value is derived with.</param>
/// <param name="options">The accounts options: the issuer, the audience and the access-token lifetime.</param>
internal sealed class TokenIssuance(AccessTokens tokens, AccountKeys keys, IOptions<AlvoAccountsOptions> options)
{
    /// <summary>RFC 8693's identifier of the token type an exchange issues.</summary>
    internal const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>The 200 for <paramref name="subject"/>'s session, carrying the refresh token <paramref name="refreshTokenId"/>.</summary>
    /// <param name="subject">The account, session and provider the access token names.</param>
    /// <param name="refreshTokenId">The id of the session's current refresh token.</param>
    /// <param name="policy">The sign-in policy read for this request, whose project name is the default audience.</param>
    /// <param name="request">The request, whose URL is the issuer in Development when none is configured.</param>
    /// <param name="exchanged">Whether this answers an exchange, which names the issued token type.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async Task<IResult> IssueAsync(
        AccessTokenSubject subject, Guid refreshTokenId, SignInPolicy policy, HttpRequest request, bool exchanged, CancellationToken ct)
    {
        var settings = options.Value;
        var access = await tokens.MintAsync(
            subject, AccountsIssuer.IssuerFor(request, settings), AccountsIssuer.AudienceFor(settings, policy), ct).ConfigureAwait(false);
        var refresh = RefreshTokenFormat.Format(refreshTokenId, await keys.RefreshMacKeyAsync(ct).ConfigureAwait(false));

        return new TokenResponse(
            access,
            exchanged ? AccessTokenType : null,
            TokenResponse.Bearer,
            settings.AccessTokenMinutes * 60,
            refresh).Ok();
    }
}
