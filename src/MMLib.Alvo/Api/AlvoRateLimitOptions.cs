namespace MMLib.Alvo.Api;

/// <summary>
/// The per-client-IP request budgets Alvo charges on its own abuse-prone routes, bound from <c>Alvo:Api:RateLimits</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each budget is a fixed one-minute window per client IP address (an IPv6 client by its <c>/64</c>), with no queue: a
/// request over it is answered 429 <c>rate-limited</c> with <c>Retry-After</c>. Alvo charges them itself, so an embedded
/// host needs no <c>UseRateLimiter()</c> step. The client address is <c>HttpContext.Connection.RemoteIpAddress</c>; a host
/// behind a proxy configures forwarded headers so it is the client's, not the proxy's.
/// </para>
/// <para>
/// <b>Each budget is named after what it limits, and no two routes share one</b> (#542 item 3): the OAuth token endpoint,
/// creating a tenant, minting an API token, and the invitation routes. Each act keeps its own hard cap besides
/// (<c>maxPerAccount</c>, 100 live tokens per account), so a budget slows a client down and never stands in for one.
/// </para>
/// </remarks>
public sealed class AlvoRateLimitOptions
{
    /// <summary>
    /// Gets or sets the OAuth token endpoint's budget (<c>POST {prefix}/token</c> — RFC 6749 §3.2's "token endpoint": the
    /// exchange and the refresh grant), per minute. Default 30.
    /// </summary>
    public int TokenEndpoint { get; set; } = 30;

    /// <summary>
    /// Gets or sets the budget for creating a tenant (<c>POST {prefix}/tenants</c>), per minute, on a host that maps the
    /// sign-in routes. Listing, renaming and managing members are not charged. Default 10.
    /// </summary>
    public int Tenants { get; set; } = 10;

    /// <summary>
    /// Gets or sets the budget for minting a personal or service token (<c>POST {prefix}/tokens</c>, switched on by the
    /// descriptor's <c>auth.apiTokens</c>), per minute, on a host that maps the sign-in routes. Listing and revoking are not
    /// charged. Default 10.
    /// </summary>
    public int ApiTokens { get; set; } = 10;

    /// <summary>
    /// Gets or sets the invitations budget, per minute, charged on every invitation route on a host that maps the sign-in
    /// routes (<c>{prefix}/tenants/{id}/invitations</c>, <c>{prefix}/invitations/preview</c> and
    /// <c>{prefix}/invitations/accept</c>). Default 20.
    /// </summary>
    public int Invitations { get; set; } = 20;
}
