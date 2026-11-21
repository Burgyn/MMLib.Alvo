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
/// <b>An <c>azp</c>, when present, must be a configured client id too</b> (#544; §3.1.3.7 item 5, "the Client SHOULD
/// verify that its client_id is the Claim Value", read as the set of client ids this deployment configures — web, iOS,
/// Android — since Alvo is one relying party fronting several OAuth clients). <c>aud</c> alone is not enough for Google: a
/// Google Cloud service account can mint an ID token whose <c>aud</c> it chooses freely, ours included, signed by Google;
/// its <c>azp</c> is the service account's own numeric id, so under <c>signUp: open</c> any GCP project could create
/// accounts by script. A person's token carries the OAuth client they signed in through in <c>azp</c>, which a mobile app's
/// configuration lists beside the web client. <b>An absent <c>azp</c> requires a single <c>aud</c></b> (§3.1.3.7 item 4,
/// "If the ID Token contains multiple audiences, the Client SHOULD verify that an azp Claim is present"); otherwise it
/// changes nothing, and a single-audience token with no <c>azp</c> still passes on <c>aud</c> alone — what closes the
/// service-account hole is that Google puts an <c>azp</c> in every such token. An empty string or JSON <c>null</c> is
/// refused, never read as absent; a number or an array never gets here — IdentityModel reads <c>azp</c> as a string and
/// refuses the token as malformed. This withdraws the S2 plan's recorded deviation ("<c>azp</c> is not checked") and
/// implements option A of the hardening design's §4.6 (ruling Q8).
/// </para>
/// <para>
/// <b>An <c>azp</c> refusal reuses <see cref="IdTokenRefusal.Audience"/></b>, the answer another app's <c>aud</c> gets.
/// The design's separate <c>IdTokenRefusal.AuthorizedParty</c> was deliberately not added: one value keeps the two
/// indistinguishable to a caller (no oracle on which claim failed), and the operator's log line tells them apart.
/// </para>
/// <para>
/// <b>Nothing about the token is logged</b>: a refusal is a value, and IdentityModel's PII switch stays off. Two lines
/// reach the operator only: an unreachable provider (its name and authority), and an audience refusal at Information
/// naming the provider and <em>which</em> claim — <c>aud</c> or <c>azp</c> — failed to name a client it is configured with,
/// never the claim's value. The caller's answer is the same either way; the line is how an operator tells a mobile client id
/// missing from <c>ClientIds</c> from a stranger's token.
/// </para>
/// </remarks>
internal sealed partial class OidcIdTokenVerifier : IIdentityVerifier, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> discovery documents and keys are fetched with.</summary>
    internal const string HttpClientName = "alvo-accounts-oidc";

    private const string AudienceClaim = "aud";

    private const string AuthorizedPartyClaim = "azp";

    private static readonly TimeSpan _skew = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan _maxAge = TimeSpan.FromMinutes(5);

    private readonly OidcProviders _providers;

    private readonly TimeProvider _clock;

    private readonly Dictionary<string, OidcMetadata> _metadata;

    private readonly JsonWebTokenHandler _handler = new();

    private readonly ILogger _logger;

    /// <summary>Creates the verifier.</summary>
    /// <param name="providers">The configured providers.</param>
    /// <param name="clients">Where the <see cref="HttpClientName"/> client comes from.</param>
    /// <param name="clock">The clock every lifetime is judged on.</param>
    /// <param name="logger">Where an unreachable provider and an audience refusal are reported, to the operator only.</param>
    public OidcIdTokenVerifier(
        OidcProviders providers, IHttpClientFactory clients, TimeProvider clock, ILogger<OidcIdTokenVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
        _clock = clock;
        _logger = logger;
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
            var refusal = RefusalOf(result.Exception);
            return refusal == IdTokenRefusal.Audience ? RefusedForAClient(provider, AudienceClaim) : IdentityVerification.Refused(refusal);
        }

        if (ForeignClientClaim(provider, token) is { } claim)
        {
            return RefusedForAClient(provider, claim);
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

    /// <summary>The claim — <c>aud</c> or <c>azp</c> — that fails to name a client the provider is configured with, if either does.</summary>
    private static string? ForeignClientClaim(OidcProvider provider, JsonWebToken token)
    {
        if (!OnlyOurAudiences(provider, token))
        {
            return AudienceClaim;
        }

        return OurAuthorizedParty(provider, token) ? null : AuthorizedPartyClaim;
    }

    private IdentityVerification RefusedForAClient(OidcProvider provider, string claim)
    {
        ClientRefused(_logger, provider.Name, claim);
        return IdentityVerification.Refused(IdTokenRefusal.Audience);
    }

    private static bool OnlyOurAudiences(OidcProvider provider, JsonWebToken token)
        => token.Audiences.All(audience => provider.ClientIds.Contains(audience, StringComparer.Ordinal));

    /// <summary>
    /// Whether the token's <c>azp</c> is a string naming a configured client id, or absent beside a single <c>aud</c> —
    /// an empty one or JSON <c>null</c> never is.
    /// </summary>
    private static bool OurAuthorizedParty(OidcProvider provider, JsonWebToken token)
        => token.TryGetPayloadValue<object>(AuthorizedPartyClaim, out var azp)
            ? azp is string party && provider.ClientIds.Contains(party, StringComparer.Ordinal)
            : token.Audiences.Count() == 1;

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

    [LoggerMessage(
        EventId = 6403,
        Level = LogLevel.Information,
        Message = "Identity provider {Provider} refused an ID token on its {Claim} claim, which must name a client id the "
            + "provider is configured with (a token without azp must have a single aud). The caller was told only that the "
            + "token was refused. If the token came from your own app signing in through another OAuth client (Android, "
            + "iOS), add that client id to the provider's ClientIds.")]
    private static partial void ClientRefused(ILogger logger, string provider, string claim);
}
