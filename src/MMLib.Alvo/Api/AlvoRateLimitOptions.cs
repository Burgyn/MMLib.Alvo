namespace MMLib.Alvo.Api;

/// <summary>
/// The per-client-IP request budgets Alvo charges on its own abuse-prone routes, bound from <c>Alvo:Api:RateLimits</c>.
/// </summary>
/// <remarks>
/// Each budget is a fixed one-minute window per client IP address (an IPv6 client by its <c>/64</c>), with no queue: a
/// request over it is answered 429 <c>rate-limited</c> with <c>Retry-After</c>. Alvo charges them itself, so an embedded
/// host needs no <c>UseRateLimiter()</c> step. The client address is <c>HttpContext.Connection.RemoteIpAddress</c>; a host
/// behind a proxy configures forwarded headers so it is the client's, not the proxy's.
/// </remarks>
public sealed class AlvoRateLimitOptions
{
    /// <summary>Gets or sets the token endpoint's budget (<c>POST {prefix}/token</c>), per minute. Default 30.</summary>
    public int Token { get; set; } = 30;

    /// <summary>
    /// Gets or sets the tenant-creation budget (<c>POST {prefix}/tenants</c> on a host that maps the sign-in routes), per
    /// minute. Default 10.
    /// </summary>
    public int Create { get; set; } = 10;

    /// <summary>
    /// Gets or sets the invitations budget, per minute, charged on every invitation route on a host that maps the sign-in
    /// routes (<c>{prefix}/tenants/{id}/invitations</c>, <c>{prefix}/invitations/preview</c> and
    /// <c>{prefix}/invitations/accept</c>). Default 20.
    /// </summary>
    public int Invitations { get; set; } = 20;
}
