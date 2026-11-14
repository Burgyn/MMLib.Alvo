namespace MMLib.Alvo.Accounts;

/// <summary>
/// End-user sign-in's configuration, bound from <see cref="AlvoAccounts.ConfigurationSection"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Infrastructure, never the descriptor.</b> The descriptor says <em>which</em> providers an app offers and who may
/// sign up (<c>auth.providers</c>, <c>auth.signUp</c>); this says how this deployment reaches them — the OAuth client
/// ids, the issuer it signs as, and how long what it issues lives. A provider signs a person in only when both name
/// it.
/// </para>
/// <para>
/// <b>Checked at startup, every refusal at once, each naming its key.</b> A misconfiguration fails the start rather
/// than the first sign-in, because the first sign-in is a person on a phone who cannot read a log.
/// </para>
/// </remarks>
public sealed class AlvoAccountsOptions
{
    /// <summary>Gets or sets the path the sign-in routes are mapped under.</summary>
    /// <remarks>Must start with <c>/</c>. The default keeps the auth routes apart from the Data API's.</remarks>
    public string Prefix { get; set; } = "/auth/v1";

    /// <summary>Gets or sets the issuer (<c>iss</c>) every access token is signed as.</summary>
    /// <remarks>
    /// <para>
    /// An absolute <c>https</c> URI, conventionally the public URL of <see cref="Prefix"/>. Required outside the
    /// <c>Development</c> environment: a token's issuer is what a client and a second replica check it against, so
    /// guessing it from an incoming request's host would let that request choose it.
    /// </para>
    /// <para>
    /// <b>Unset in <c>Development</c>, it is the URL the request reached the sign-in routes at</b> — scheme, host, path
    /// base and <see cref="Prefix"/>, so <c>http://localhost:5000/auth/v1</c> on a local run — read per request, when a
    /// token is minted and when one is validated. That is the dev loop's convenience and nothing more: a token minted
    /// through one host name does not validate through another. Set, it must be <c>https</c> in every environment, so a
    /// local <c>http</c> issuer is spelled by leaving this unset.
    /// </para>
    /// </remarks>
    public string? Issuer { get; set; }

    /// <summary>Gets or sets the audience (<c>aud</c>) every access token is issued for.</summary>
    /// <remarks>When unset, the applied descriptor's name.</remarks>
    public string? Audience { get; set; }

    /// <summary>Gets or sets how long an access token is valid, in minutes — 1 to 60.</summary>
    /// <remarks>
    /// Short on purpose. Alvo reads the session on every request, so a disabled account or an ended session is refused
    /// at once here; this is how long a copy of the token can still be honoured by anything that checks only its
    /// signature against the published keys.
    /// </remarks>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Gets or sets how long a session survives without a refresh, in days — at least 1.</summary>
    public int RefreshIdleDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets how long a session lives however often it is refreshed, in days — at least
    /// <see cref="RefreshIdleDays"/>.
    /// </summary>
    public int SessionLifetimeDays { get; set; } = 180;

    /// <summary>
    /// Gets or sets how long, in seconds, a refresh token that was just rotated still yields the same child — 0 to 60.
    /// </summary>
    /// <remarks>
    /// Two tabs refreshing at once present the same token; inside this window both get the one child and the session
    /// survives. Presented after it, the rotated token is a replay, and every token of the session is revoked.
    /// </remarks>
    public int RefreshReuseGraceSeconds { get; set; } = 10;

    /// <summary>Gets the identity providers this deployment can verify a person's ID token from, by name.</summary>
    /// <remarks>
    /// Case-insensitive, because configuration keys are: <c>Alvo__Accounts__Providers__google__ClientIds__0</c> binds
    /// as typed. This build offers <c>Google</c>; any other name is refused at startup.
    /// </remarks>
    public Dictionary<string, AlvoOidcProviderOptions> Providers { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}
