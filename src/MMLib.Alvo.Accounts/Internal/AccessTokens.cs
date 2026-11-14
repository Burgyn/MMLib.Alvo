using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>Who an access token says its bearer is.</summary>
/// <param name="Account">The account (<c>sub</c>).</param>
/// <param name="Session">The session (<c>sid</c>).</param>
/// <param name="Provider">The provider the session began with (<c>idp</c>).</param>
internal sealed record AccessTokenSubject(UserId Account, Guid Session, string Provider);

/// <summary>
/// Mints and validates Alvo's access tokens: JWTs per RFC 9068, ES256, header <c>typ: at+jwt</c> and <c>kid</c>, carrying
/// <c>iss</c>, <c>aud</c>, <c>sub</c>, <c>sid</c>, <c>iat</c>, <c>exp</c>, <c>jti</c>, <c>client_id</c> and <c>idp</c> —
/// and nothing else (spec §5.4, D5: no email, roles or tenant; what a person may do is read per request).
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation is pinned, not negotiated</b> (RFC 8725 §3.1, §3.11): the type must be <c>at+jwt</c>, the algorithm
/// <c>ES256</c>, the key the one the <c>kid</c> names — never every key in turn — and the issuer and audience exact.
/// Lifetime is checked against the injected clock with a 30-second skew (S2 D5), not by the handler.
/// </para>
/// <para>
/// <b>Every refusal is the same <see langword="null"/></b>, so a caller cannot tell a forged token from an expired one,
/// and nothing about the token reaches a log: IdentityModel's own PII switch stays off.
/// </para>
/// </remarks>
/// <param name="keys">The signing keys.</param>
/// <param name="options">The accounts options, for the access-token lifetime.</param>
/// <param name="clock">The clock every instant comes from.</param>
internal sealed class AccessTokens(AccountKeys keys, IOptions<AlvoAccountsOptions> options, TimeProvider clock)
{
    /// <summary>The <c>typ</c> header of an access token (RFC 9068 §2.1).</summary>
    internal const string TokenType = "at+jwt";

    /// <summary>The <c>client_id</c> Alvo's own clients share until client registration exists (spec §5.4).</summary>
    internal const string ClientId = "alvo";

    /// <summary>How far past its <c>exp</c> (or before its <c>nbf</c>) an access token is still honoured.</summary>
    internal static TimeSpan Skew { get; } = TimeSpan.FromSeconds(30);

    private const string SessionClaim = "sid";

    private const string ClientIdClaim = "client_id";

    private const string ProviderClaim = "idp";

    private static readonly string[] _types = [TokenType];

    private static readonly string[] _algorithms = [SecurityAlgorithms.EcdsaSha256];

    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>Mints an access token for <paramref name="subject"/>.</summary>
    /// <param name="subject">The account, session and provider the token names.</param>
    /// <param name="issuer">The issuer (<c>iss</c>).</param>
    /// <param name="audience">The audience (<c>aud</c>).</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<string> MintAsync(AccessTokenSubject subject, string issuer, string audience, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var key = await keys.SigningAsync(ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = TokenType,
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            Expires = (now + TimeSpan.FromMinutes(options.Value.AccessTokenMinutes)).UtcDateTime,
            SigningCredentials = new SigningCredentials(key.Key, SecurityAlgorithms.EcdsaSha256),
            Claims = Claims(subject),
        });
    }

    /// <summary>
    /// The subject of <paramref name="token"/> when it is a live access token this deployment issued for
    /// <paramref name="audience"/> as <paramref name="issuer"/>; otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="token">The bearer token presented.</param>
    /// <param name="issuer">The issuer it must name.</param>
    /// <param name="audience">The audience it must name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<AccessTokenSubject?> ValidateAsync(string token, string issuer, string audience, CancellationToken ct)
    {
        if (Read(token) is not { Kid: { Length: > 0 } kid } read
            || await keys.FindAsync(kid, ct).ConfigureAwait(false) is not { } key)
        {
            return null;
        }

        var result = await _handler.ValidateTokenAsync(read, Parameters(key, issuer, audience)).ConfigureAwait(false);
        return result.IsValid && result.SecurityToken is JsonWebToken valid && IsLive(valid) ? SubjectOf(valid) : null;
    }

    private static Dictionary<string, object> Claims(AccessTokenSubject subject) => new(StringComparer.Ordinal)
    {
        [JwtRegisteredClaimNames.Sub] = subject.Account.Value.ToString(),
        [SessionClaim] = subject.Session.ToString("N"),
        [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
        [ClientIdClaim] = ClientId,
        [ProviderClaim] = subject.Provider,
    };

    private JsonWebToken? Read(string token)
    {
        if (!_handler.CanReadToken(token))
        {
            return null;
        }

        try
        {
            return _handler.ReadJsonWebToken(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static TokenValidationParameters Parameters(SigningKey key, string issuer, string audience) => new()
    {
        ValidIssuer = issuer,
        ValidAudience = audience,
        ValidTypes = _types,
        ValidAlgorithms = _algorithms,
        IssuerSigningKey = key.Key,
        TryAllIssuerSigningKeys = false,
        RequireSignedTokens = true,
        ValidateLifetime = false,
    };

    private bool IsLive(JsonWebToken token) => TokenTimes.Live(
        TokenTimes.Instant(token, JwtRegisteredClaimNames.Exp),
        TokenTimes.Instant(token, JwtRegisteredClaimNames.Nbf),
        clock.GetUtcNow(),
        Skew);

    private static AccessTokenSubject? SubjectOf(JsonWebToken token)
        => token.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Sub, out var sub) && Guid.TryParse(sub, out var account)
            && token.TryGetPayloadValue<string>(SessionClaim, out var sid) && Guid.TryParseExact(sid, "N", out var session)
            && token.TryGetPayloadValue<string>(ProviderClaim, out var provider) && !string.IsNullOrEmpty(provider)
            && account != Guid.Empty
                ? new AccessTokenSubject(new UserId(account), session, provider)
                : null;
}
