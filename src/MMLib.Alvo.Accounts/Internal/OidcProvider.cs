using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>One OpenID Connect provider an ID token is verified against (spec §5.2, §5.3, §5.8).</summary>
/// <param name="Name">The provider's name in the descriptor, e.g. <c>google</c>; what an identity is recorded under.</param>
/// <param name="Provider">The descriptor's provider the name spells.</param>
/// <param name="Issuers">Every exact <c>iss</c> the provider signs as.</param>
/// <param name="Authority">The URL whose <c>/.well-known/openid-configuration</c> names the provider's keys.</param>
/// <param name="ClientIds">The client ids an ID token's <c>aud</c> may name.</param>
/// <param name="Algorithms">
/// The algorithms the provider signs with; <see langword="null"/> takes the discovery document's
/// <c>id_token_signing_alg_values_supported</c>. Either way, <c>none</c> and every <c>HS*</c> are removed (RFC 8725 §3.1).
/// </param>
/// <param name="EmailTrusted">Whether a verified token's email is trusted (spec §5.3).</param>
/// <param name="AllowHttpMetadata">Whether the discovery document and keys may be fetched over plain HTTP.</param>
internal sealed record OidcProvider(
    string Name,
    AuthProvider Provider,
    IReadOnlySet<string> Issuers,
    string Authority,
    IReadOnlyList<string> ClientIds,
    IReadOnlySet<string>? Algorithms,
    Func<JsonWebToken, bool> EmailTrusted,
    bool AllowHttpMetadata)
{
    private const string EmailVerifiedClaim = "email_verified";

    /// <summary>
    /// A generic OIDC provider: its one issuer, its authority (the issuer unless configured), the discovery document's
    /// algorithms, and an email trusted only when the configuration says <c>TrustEmail</c> and the token
    /// <c>email_verified</c>.
    /// </summary>
    /// <param name="name">The provider's name in the descriptor.</param>
    /// <param name="provider">The descriptor's provider.</param>
    /// <param name="issuer">The exact <c>iss</c> the provider signs as.</param>
    /// <param name="options">The provider's configuration.</param>
    internal static OidcProvider Generic(string name, AuthProvider provider, string issuer, AlvoOidcProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(
            name,
            provider,
            new HashSet<string>(StringComparer.Ordinal) { issuer },
            options.Authority ?? issuer,
            [.. options.ClientIds],
            null,
            options.TrustEmail ? EmailVerified : static _ => false,
            options.AllowHttpMetadata);
    }

    /// <summary>
    /// Whether <paramref name="token"/>'s <c>email_verified</c> is true — as a JSON boolean, or as the string some
    /// providers send.
    /// </summary>
    /// <param name="token">A validated ID token.</param>
    internal static bool EmailVerified(JsonWebToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.TryGetPayloadValue<bool>(EmailVerifiedClaim, out var verified))
        {
            return verified;
        }

        return token.TryGetPayloadValue<string>(EmailVerifiedClaim, out var text)
            && string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
    }
}
