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
}
