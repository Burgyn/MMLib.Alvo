namespace MMLib.Alvo.Accounts;

/// <summary>
/// One OpenID Connect provider an ID token is verified against, under <see cref="AlvoAccountsOptions.Providers"/>.
/// </summary>
public sealed class AlvoOidcProviderOptions
{
    /// <summary>Gets the OAuth client ids an ID token's audience (<c>aud</c>) may name — at least one.</summary>
    /// <remarks>
    /// One per client the app ships (a web client, an iOS client, …): every one of them signs a person in to the same
    /// accounts, and a token issued to any other client is refused.
    /// </remarks>
    public List<string> ClientIds { get; } = [];

    /// <summary>Gets or sets the provider's issuer URL, whose discovery document names its signing keys.</summary>
    /// <remarks>
    /// When unset, the provider's own (<c>https://accounts.google.com</c> for Google). Overridable for a test issuer.
    /// Must be <c>https</c> unless <see cref="AllowHttpMetadata"/> is set.
    /// </remarks>
    public string? Authority { get; set; }

    /// <summary>Gets or sets a value indicating whether an <c>http://</c> <see cref="Authority"/> is accepted.</summary>
    /// <remarks>
    /// For an end-to-end stack's test issuer only: keys fetched over plain HTTP can be replaced by anyone on the path,
    /// and with them every identity the provider asserts. Setting it logs a warning on every start.
    /// </remarks>
    public bool AllowHttpMetadata { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a generic provider's verified email (<c>email_verified</c>) is trusted.
    /// </summary>
    /// <remarks>
    /// Ignored for Google, whose own rule decides (a verified <c>@gmail.com</c> address or a Workspace domain).
    /// </remarks>
    public bool TrustEmail { get; set; }
}
