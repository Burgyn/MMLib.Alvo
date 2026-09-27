using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Admin;
using System.Globalization;
using System.Threading.RateLimiting;

namespace MMLib.Alvo.Host.Internal;

/// <summary>
/// The one rate limit both credential forms share: sign-in and set-password (design §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>One named policy, so one budget per client across both forms.</b> A policy's partitions are the policy's own,
/// so two endpoints that name it draw from the same fixed window for the same client: a stranger who spends the
/// minute on sign-in guesses has nothing left for token redemption, and the reverse. The partition is
/// <c>Connection.RemoteIpAddress</c>, which is the client only when the host sees the client — behind a proxy that is
/// true only with <c>Alvo:ForwardedHeaders:Enabled</c> on (docs/architecture/host.md); without it every client behind
/// the proxy shares one budget, which fails closed rather than open.
/// </para>
/// <para>
/// <b>What it bounds.</b> Identity's per-account lockout already stops password guessing on sign-in; it does not apply
/// to redemption, and needs not, because a token is a MAC'd data-protection payload nobody can guess. The limit bounds
/// what the lockout does not: the hashing cost of the success path, and spraying one password across many
/// addresses. A fixed window, no queue, no CAPTCHA (design §8.7).
/// </para>
/// <para>
/// <b>The count is configuration, not an option</b>: <c>Alvo:Admin:CredentialAttemptsPerMinute</c>, read here and
/// validated at start, so the end-to-end world can raise it and the throttling scenario lower it without the host's
/// public options growing a member for a test.
/// </para>
/// </remarks>
internal static class AlvoAdminCredentialLimit
{
    /// <summary>The policy both credential posts name.</summary>
    internal const string PolicyName = "alvo-admin-credentials";

    /// <summary>Where the number of attempts per minute is read from.</summary>
    internal const string ConfigurationKey = "Alvo:Admin:CredentialAttemptsPerMinute";

    /// <summary>The same key, as a container sets it.</summary>
    internal const string Variable = "Alvo__Admin__CredentialAttemptsPerMinute";

    /// <summary>The attempts a client has per minute when nothing says otherwise.</summary>
    internal const int DefaultAttemptsPerMinute = 20;

    /// <summary>Registers the policy, its refusal, and the validated count.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">The configuration the count is read from.</param>
    internal static void AddAlvoAdminCredentialLimit(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AlvoAdminCredentialLimitOptions>()
            .Configure(limit => limit.Read(configuration[ConfigurationKey]))
            .Validate(limit => limit.AttemptsPerMinute > 0, NotPositive(configuration[ConfigurationKey]))
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(PolicyName, Partition);
            limiter.OnRejected = RejectedAsync;
        });
    }

    /// <summary>One fixed window per client address.</summary>
    /// <param name="http">The request.</param>
    private static RateLimitPartition<string> Partition(HttpContext http)
    {
        var perMinute = http.RequestServices.GetRequiredService<IOptions<AlvoAdminCredentialLimitOptions>>()
            .Value.AttemptsPerMinute;

        return RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = perMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    }

    /// <summary>
    /// Sends a throttled post back to the page it came from, with <c>throttled=true</c> and a <c>Retry-After</c>.
    /// </summary>
    /// <remarks>
    /// A redirect, never the limiter's bare status: a browser shows a form post's <c>429</c> or <c>503</c> as a blank
    /// error page, and the person has no idea that waiting a minute would work.
    /// </remarks>
    /// <param name="context">The refused request and its lease.</param>
    /// <param name="cancellationToken">Unused: nothing here waits.</param>
    private static ValueTask RejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var page = http.Request.Path.StartsWithSegments(AlvoAdmin.SetPasswordEndpoint, StringComparison.Ordinal)
            ? AlvoAdmin.SetPasswordPath
            : AlvoAdmin.SignInPath;
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromMinutes(1);

        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        _ = AlvoAdminRedirect.SeeOther(http, $"{page}?throttled=true");
        return ValueTask.CompletedTask;
    }

    /// <summary>The refusal for a count that is not a whole number above zero.</summary>
    /// <param name="configured">What configuration said.</param>
    private static string NotPositive(string? configured) => string.Join(
        Environment.NewLine,
        $"Alvo cannot start: {Variable} is '{configured}', and it has to be a whole number above zero.",
        string.Empty,
        $"  Set:        {Variable}={DefaultAttemptsPerMinute}, or unset it for that default.");
}

/// <summary>The credential limit's one number, read from configuration and validated at start.</summary>
internal sealed class AlvoAdminCredentialLimitOptions
{
    /// <summary>How many credential posts one client may make per minute, across both forms.</summary>
    public int AttemptsPerMinute { get; set; } = AlvoAdminCredentialLimit.DefaultAttemptsPerMinute;

    /// <summary>Takes the configured text, where anything that is not a whole number reads as zero and is refused.</summary>
    /// <param name="configured">What configuration said, or <see langword="null"/> when it said nothing.</param>
    internal void Read(string? configured)
        => AttemptsPerMinute = configured is null ? AlvoAdminCredentialLimit.DefaultAttemptsPerMinute
            : int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
