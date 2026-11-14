using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

namespace MMLib.Alvo.Api.Internal;

/// <summary>The routes Alvo charges a per-client budget on (spec §7.3).</summary>
internal enum AlvoRateLimit
{
    /// <summary>The token endpoint, <c>POST {prefix}/token</c>: <see cref="AlvoRateLimitOptions.Token"/> per minute.</summary>
    Token,
}

/// <summary>
/// The per-client-IP budgets of <see cref="AlvoRateLimitOptions"/>, charged by an endpoint filter on the route itself
/// (S2 D3, the <c>AlvoAdminCredentialLimit</c> precedent) — so an embedded host needs no <c>UseRateLimiter()</c> step.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fixed one-minute windows, no queue</b>: a request over the budget is answered at once, 429 <c>rate-limited</c>
/// with <c>Retry-After</c> in whole seconds until the window turns (60 when the limiter does not say).
/// </para>
/// <para>
/// <b>Partitioned by client address</b>: an IPv4-mapped IPv6 address as the IPv4 address it carries, any other IPv6
/// address by its <c>/64</c> (one host is routinely handed a whole <c>/64</c>, so a per-address budget would be one
/// per address it chooses), and a request with no address — an in-process test server, a Unix socket — all in one
/// shared partition.
/// </para>
/// </remarks>
internal sealed class AlvoRateLimits : IDisposable
{
    private const int DefaultRetryAfterSeconds = 60;

    private readonly Dictionary<AlvoRateLimit, Charged> _limits;

    /// <summary>Builds one limiter per <see cref="AlvoRateLimit"/> from the configured budgets.</summary>
    /// <param name="options">The API options, for the budgets.</param>
    public AlvoRateLimits(IOptions<AlvoApiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _limits = new() { [AlvoRateLimit.Token] = new Charged(FixedWindows(options.Value.RateLimits?.Token ?? 0)) };
    }

    /// <summary>The filter that charges <paramref name="limit"/>'s budget before the handler runs.</summary>
    /// <param name="limit">The budget to charge.</param>
    internal IEndpointFilter For(AlvoRateLimit limit) => _limits[limit];

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var limit in _limits.Values)
        {
            limit.Limiter.Dispose();
        }
    }

    private static PartitionedRateLimiter<HttpContext> FixedWindows(int perMinute)
        => PartitionedRateLimiter.Create<HttpContext, string>(http => RateLimitPartition.GetFixedWindowLimiter(
            ClientPartition.Of(http.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(perMinute, 1),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    private sealed record Charged(PartitionedRateLimiter<HttpContext> Limiter) : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);
            using var lease = Limiter.AttemptAcquire(context.HttpContext);
            return lease.IsAcquired
                ? next(context)
                : ValueTask.FromResult<object?>(ProblemResultFactory.RateLimited(RetryAfterSeconds(lease)));
        }

        private static int RetryAfterSeconds(RateLimitLease lease)
            => lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
                ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds))
                : DefaultRetryAfterSeconds;
    }
}
