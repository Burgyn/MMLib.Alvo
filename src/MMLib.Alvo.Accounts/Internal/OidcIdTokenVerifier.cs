using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Verifies an ID token per OpenID Connect Core §3.1.3.7 (spec §5.2 steps 1–3): the provider is found by the token's
/// exact <c>iss</c>, and must be one the caller lists before anything is fetched; the signature by the key its <c>kid</c> names among the provider's published keys, under an
/// algorithm the provider signs with; <c>aud</c> must name a configured client id; <c>exp</c>/<c>nbf</c> hold within two
/// minutes and <c>iat</c> is at most five minutes old, on the injected clock (S2 D5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The algorithm is checked before the signature</b>, against the provider's list less <c>none</c> and every
/// <c>HS*</c> (RFC 8725 §3.1), so neither an unsigned token nor one MACed with a public key ever reaches a key; the
/// handler's <c>ValidAlgorithms</c> repeats it. The key is the one the <c>kid</c> names, never every key in turn.
/// </para>
/// <para>
/// <b>Every <c>aud</c> must be a configured client id</b> (§3.1.3.7 item 3: a token is rejected when it "contains
/// additional audiences not trusted by the Client"), not merely one of them: a multi-audience issuer can mint
/// <c>aud: [another-app, ours]</c> for another relying party, and that token must not sign anyone in here.
/// </para>
/// <para>
/// <b>Deviation: <c>azp</c> is not checked.</b> OIDC Core errata set 2 reworded §3.1.3.7 items 4–5 so that <c>azp</c> is
/// validated only as the extension that introduced it says. Google's documented server-side check is
/// <c>aud</c> alone, and an Android or iOS sign-in carries the mobile client's id in <c>azp</c> while <c>aud</c> names the
/// web client the backend configured; requiring it would refuse Google's own mobile flow.
/// </para>
/// <para>
/// <b>Nothing about the token is logged</b>: a refusal is a value, the one log line (an unreachable provider) names the
/// provider and its authority, and IdentityModel's PII switch stays off.
/// </para>
/// </remarks>
internal sealed class OidcIdTokenVerifier : IIdentityVerifier, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> discovery documents and keys are fetched with.</summary>
    internal const string HttpClientName = "alvo-accounts-oidc";

    private static readonly TimeSpan _skew = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan _maxAge = TimeSpan.FromMinutes(5);

    private readonly OidcProviders _providers;

    private readonly TimeProvider _clock;

    private readonly Dictionary<string, OidcMetadata> _metadata;

    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Creates the verifier.</summary>
    /// <param name="providers">The configured providers.</param>
    /// <param name="clients">Where the <see cref="HttpClientName"/> client comes from.</param>
    /// <param name="clock">The clock every lifetime is judged on.</param>
    /// <param name="logger">Where an unreachable provider is reported.</param>
    public OidcIdTokenVerifier(
        OidcProviders providers, IHttpClientFactory clients, TimeProvider clock, ILogger<OidcIdTokenVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
        _clock = clock;
        _metadata = providers.All.ToDictionary(
            provider => provider.Name, provider => new OidcMetadata(provider, clients, clock, logger), StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var metadata in _metadata.Values)
        {
            metadata.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IdentityVerification> VerifyAsync(string subjectToken, Func<string, bool> listed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listed);
        if (Read(subjectToken) is not { } token)
        {
            return IdentityVerification.Refused(IdTokenRefusal.Malformed);
        }

        if (!_providers.TryFindByIssuer(token.Issuer, out var provider))
        {
            return IdentityVerification.Refused(IdTokenRefusal.UnknownIssuer);
        }

        return listed(provider.Name)
            ? await VerifyAsync(subjectToken, token, provider, ct).ConfigureAwait(false)
            : IdentityVerification.Refused(IdTokenRefusal.NotListed);
    }

    private async ValueTask<IdentityVerification> VerifyAsync(
        string subjectToken, JsonWebToken token, OidcProvider provider, CancellationToken ct)
    {
        var metadata = _metadata[provider.Name];
        if (await metadata.GetAsync(ct).ConfigureAwait(false) is not { } configuration)
        {
            return IdentityVerification.Refused(IdTokenRefusal.Unreachable);
        }

        var algorithms = AlgorithmsOf(provider, configuration);
        if (!algorithms.Contains(token.Alg))
        {
            return IdentityVerification.Refused(IdTokenRefusal.Algorithm);
        }

        if (!Publishes(configuration, token.Kid))
        {
            configuration = await metadata.RefreshForUnknownKidAsync(configuration, ct).ConfigureAwait(false);
        }

        var result = await _handler.ValidateTokenAsync(token, Parameters(provider, configuration, algorithms)).ConfigureAwait(false);
        if (!result.IsValid)
        {
            return IdentityVerification.Refused(RefusalOf(result.Exception));
        }

        if (!OnlyOurAudiences(provider, token))
        {
            return IdentityVerification.Refused(IdTokenRefusal.Audience);
        }

        return TimeRefusal(token) is { } late ? IdentityVerification.Refused(late) : Verified(provider, token, subjectToken);
    }

    /// <summary>
    /// The token as a signed JWT naming an algorithm and an issuer, or <see langword="null"/> (malformed). IdentityModel
    /// reads a JSON-<c>null</c> <c>iss</c> as a <see langword="null"/> <see cref="JsonWebToken.Issuer"/> and an absent
    /// one as empty; neither may reach the issuer lookup.
    /// </summary>
    private JsonWebToken? Read(string subjectToken)
    {
        if (string.IsNullOrEmpty(subjectToken) || !_handler.CanReadToken(subjectToken))
        {
            return null;
        }

        try
        {
            var token = _handler.ReadJsonWebToken(subjectToken);
            return token.IsEncrypted || string.IsNullOrEmpty(token.Alg) || string.IsNullOrEmpty(token.Issuer) ? null : token;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string[] AlgorithmsOf(OidcProvider provider, OpenIdConnectConfiguration configuration)
        => [.. (provider.Algorithms ?? (IEnumerable<string>)configuration.IdTokenSigningAlgValuesSupported).Where(IsAsymmetric)];

    private static bool IsAsymmetric(string algorithm)
        => !string.Equals(algorithm, "none", StringComparison.OrdinalIgnoreCase)
            && !algorithm.StartsWith("HS", StringComparison.OrdinalIgnoreCase);

    private static bool Publishes(OpenIdConnectConfiguration configuration, string kid)
        => configuration.SigningKeys.Any(key => string.Equals(key.KeyId, kid, StringComparison.Ordinal));

    private static bool OnlyOurAudiences(OidcProvider provider, JsonWebToken token)
        => token.Audiences.All(audience => provider.ClientIds.Contains(audience, StringComparer.Ordinal));

    private static TokenValidationParameters Parameters(
        OidcProvider provider, OpenIdConnectConfiguration configuration, string[] algorithms) => new()
        {
            ValidIssuers = provider.Issuers,
            ValidAudiences = provider.ClientIds,
            ValidAlgorithms = algorithms,
            IssuerSigningKeys = configuration.SigningKeys,
            TryAllIssuerSigningKeys = false,
            RequireSignedTokens = true,
            ValidateLifetime = false,
        };

    private static IdTokenRefusal RefusalOf(Exception? failure) => failure switch
    {
        SecurityTokenInvalidAudienceException => IdTokenRefusal.Audience,
        SecurityTokenInvalidAlgorithmException => IdTokenRefusal.Algorithm,
        SecurityTokenInvalidIssuerException => IdTokenRefusal.UnknownIssuer,
        SecurityTokenMalformedException => IdTokenRefusal.Malformed,
        _ => IdTokenRefusal.Signature,
    };

    private IdTokenRefusal? TimeRefusal(JsonWebToken token)
    {
        var now = _clock.GetUtcNow();
        var notBefore = TokenTimes.Instant(token, JwtRegisteredClaimNames.Nbf);
        if (TokenTimes.NotYetValid(notBefore, now, _skew))
        {
            return IdTokenRefusal.NotYetValid;
        }

        if (!TokenTimes.Live(TokenTimes.Instant(token, JwtRegisteredClaimNames.Exp), notBefore, now, _skew))
        {
            return IdTokenRefusal.Expired;
        }

        return TokenTimes.Fresh(TokenTimes.Instant(token, JwtRegisteredClaimNames.Iat), now, _maxAge, _skew)
            ? null
            : IdTokenRefusal.Stale;
    }

    private static IdentityVerification Verified(OidcProvider provider, JsonWebToken token, string subjectToken)
    {
        if (string.IsNullOrEmpty(token.Subject))
        {
            return IdentityVerification.Refused(IdTokenRefusal.Malformed);
        }

        var email = Claim(token, JwtRegisteredClaimNames.Email);
        var identity = new VerifiedIdentity(
            provider.Name,
            token.Subject,
            email,
            email is not null && provider.EmailTrusted(token),
            Claim(token, JwtRegisteredClaimNames.Name),
            Claim(token, "picture"));
        return new IdentityVerification(identity, SubjectTokenId(token, subjectToken), null);
    }

    private static string? Claim(JsonWebToken token, string name)
        => token.TryGetPayloadValue<string>(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>The single-use id of an ID token: its issuer and <c>jti</c>, or its issuer and its own hash.</summary>
    /// <param name="token">The validated token.</param>
    /// <param name="subjectToken">The token as presented.</param>
    internal static string SubjectTokenId(JsonWebToken token, string subjectToken)
    {
        var jti = Claim(token, JwtRegisteredClaimNames.Jti) ?? Hash(subjectToken);
        return Hash($"{token.Issuer}\n{jti}");
    }

    private static string Hash(string text) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
