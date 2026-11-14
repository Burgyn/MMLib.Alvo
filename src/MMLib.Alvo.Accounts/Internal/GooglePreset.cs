using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Google as an OIDC provider (S2 D6, Google's <i>Verify the Google ID token on your server side</i>): both issuer
/// spellings Google documents, <c>RS256</c> only, and an email trusted only when it is verified and Google is
/// authoritative for it — a <c>@gmail.com</c> address, or a Workspace account (<c>hd</c>).
/// </summary>
internal static class GooglePreset
{
    /// <summary>The provider's name in the descriptor.</summary>
    internal const string Name = "google";

    /// <summary>Google's issuer URL, whose discovery document names its keys.</summary>
    internal const string Authority = "https://accounts.google.com";

    private const string HostedDomainClaim = "hd";

    private const string GmailSuffix = "@gmail.com";

    /// <summary>Gets both spellings of <c>iss</c> Google signs with.</summary>
    internal static IReadOnlySet<string> Issuers { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Authority, "accounts.google.com" };

    private static IReadOnlySet<string> Algorithms { get; } = new HashSet<string>(StringComparer.Ordinal) { "RS256" };

    /// <summary>The Google provider <paramref name="options"/> configure.</summary>
    /// <param name="options">The configured client ids and, for a test issuer, the authority.</param>
    internal static OidcProvider From(AlvoOidcProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(
            Name,
            AuthProvider.Google,
            Issuers,
            options.Authority ?? Authority,
            [.. options.ClientIds],
            Algorithms,
            TrustsEmail,
            options.AllowHttpMetadata);
    }

    /// <summary>Whether Google vouches for <paramref name="token"/>'s email (spec §5.3).</summary>
    /// <param name="token">A validated Google ID token.</param>
    internal static bool TrustsEmail(JsonWebToken token)
        => OidcProvider.EmailVerified(token)
            && token.TryGetPayloadValue<string>("email", out var email)
            && (email.EndsWith(GmailSuffix, StringComparison.OrdinalIgnoreCase) || IsHosted(token));

    private static bool IsHosted(JsonWebToken token)
        => token.TryGetPayloadValue<string>(HostedDomainClaim, out var domain) && !string.IsNullOrEmpty(domain);
}
