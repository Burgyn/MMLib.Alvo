using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Admin;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace MMLib.Alvo.Host.Internal;

/// <summary>
/// The rate limit on the two credential forms, sign-in and set-password (design §2, as amended by §8.7 and ruling
/// 10.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two layers, so locking out everyone behind one address takes ten times the effort.</b> A single budget per
/// client address let one stranger spend it for a whole office behind a NAT, or for every user behind a proxy that
/// forwards no client address. So the design's budget (20 a minute) is charged per <em>subject</em>: per client and
/// normalised address on sign-in, per client and token on set-password. A flood for one address throttles that
/// address from that client and nobody else. Over it sits a coarse <em>ceiling</em> per client (200 a minute), shared
/// by both forms, which bounds what the subjects cannot: the password-hashing cost of a client that cycles addresses
/// or tokens.
/// </para>
/// <para>
/// <b>What remains, accepted (ruling 10.8):</b> a client that cycles 200 distinct subjects a minute, each with a valid
/// anonymous antiforgery pair, spends the ceiling, and so still throttles everyone behind its address. Requests with no
/// remote address (an in-process or Unix-socket host) all share one partition. And Identity's per-account lockout,
/// which this does not replace, lets anyone keep a known account locked with a wrong password every few minutes
/// (todo item 46).
/// </para>
/// <para>
/// <b>Memory is bounded by the ceiling, not by the request rate.</b> The ceiling is checked, without spending it,
/// before a subject's partition is created, so a client the ceiling refuses creates none; and every subject is a
/// fixed-length hash, so no key holds what a client typed. At most one ceiling's worth of subject partitions per client
/// is alive per window.
/// </para>
/// <para>
/// <b>Charged by the endpoint, after the antiforgery check</b>, never by middleware in front of it. A cross-site page
/// in a victim's browser posts without a valid antiforgery pair, so it cannot spend the victim's budget; a request
/// middleware counts before the endpoint can tell. Antiforgery validation is cheap, and it is all such a post costs.
/// </para>
/// <para>
/// <b>A client is its address, an IPv6 one by its /64</b>: an IPv6 host is routinely handed a whole /64, so a /128
/// partition would give it unlimited budgets. An IPv4-mapped address is the IPv4 one, so a dual-stack client has one
/// budget. Behind a proxy the address is the client only with <c>Alvo:ForwardedHeaders:Enabled</c> on
/// (docs/architecture/host.md).
/// </para>
/// <para>
/// <b>The counts are configuration, not options</b>: <c>Alvo:Admin:CredentialAttemptsPerMinute</c> and
/// <c>Alvo:Admin:CredentialCeilingPerMinute</c>, validated at start when the dashboard is on, so the end-to-end
/// world can raise them and a throttling fact lower them without the host's public options growing a member.
/// </para>
/// </remarks>
internal sealed class AlvoAdminCredentialLimit : IDisposable
{
    /// <summary>Where the per-subject attempts per minute are read from.</summary>
    internal const string AttemptsKey = "Alvo:Admin:CredentialAttemptsPerMinute";

    /// <summary>Where the per-client ceiling per minute is read from.</summary>
    internal const string CeilingKey = "Alvo:Admin:CredentialCeilingPerMinute";

    /// <summary>The characters of a token that name its subject; past the data-protection header, into the random part.</summary>
    private const int TokenPrefixLength = 64;

    private readonly PartitionedRateLimiter<string> _subjects;
    private readonly PartitionedRateLimiter<string> _clients;
    private int _subjectPartitionsCreated;

    /// <summary>Builds the two limiters from the validated counts.</summary>
    /// <param name="options">The counts.</param>
    public AlvoAdminCredentialLimit(IOptions<AlvoAdminCredentialLimitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _subjects = FixedWindows(options.Value.AttemptsPerMinute, () => Interlocked.Increment(ref _subjectPartitionsCreated));
        _clients = FixedWindows(options.Value.CeilingPerMinute, () => { });
    }

    /// <summary>Registers the limit and its validated counts.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">The configuration the counts are read from.</param>
    internal static void AddAlvoAdminCredentialLimit(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AlvoAdminCredentialLimitOptions>()
            .Configure(limit => limit.Read(configuration[AttemptsKey], configuration[CeilingKey]))
            .Validate<IOptions<AlvoAdminOptions>>(
                (limit, admin) => !admin.Value.Enabled || limit.AttemptsPerMinute > 0,
                NotPositive(AttemptsKey, configuration[AttemptsKey], AlvoAdminCredentialLimitOptions.DefaultAttempts))
            .Validate<IOptions<AlvoAdminOptions>>(
                (limit, admin) => !admin.Value.Enabled || limit.CeilingPerMinute > 0,
                NotPositive(CeilingKey, configuration[CeilingKey], AlvoAdminCredentialLimitOptions.DefaultCeiling))
            .ValidateOnStart();
        services.AddSingleton<AlvoAdminCredentialLimit>();
    }

    /// <summary>
    /// A sign-in's subject: a hash of the address as the registered <see cref="ILookupNormalizer"/> normalises it, so
    /// every spelling Identity finds as one account is one budget, and the key is a fixed length.
    /// </summary>
    /// <param name="normalized">The address, through <see cref="ILookupNormalizer.NormalizeEmail"/>.</param>
    internal static string SignInSubject(string normalized) => $"sign-in|{Hash(normalized)}";

    /// <summary>
    /// A set-password post's subject: a hash of the token's first characters, never the token itself, so the
    /// limiter's keys hold no credential.
    /// </summary>
    /// <param name="token">The repaired token.</param>
    internal static string SetPasswordSubject(string token)
    {
        var prefix = token.Length > TokenPrefixLength ? token[..TokenPrefixLength] : token;
        return $"set-password|{Hash(prefix)}";
    }

    /// <summary>How many subject partitions have been created, for the fact that the ceiling bounds them.</summary>
    internal int SubjectPartitionsCreated => Volatile.Read(ref _subjectPartitionsCreated);

    /// <summary>The partition a client address falls in: IPv4 as is, IPv6 by its /64, IPv4-mapped as IPv4.</summary>
    /// <param name="address">The connection's remote address, when known.</param>
    internal static string ClientOf(IPAddress? address)
    {
        if (address is null)
        {
            return string.Empty;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>
    /// Charges one attempt at <paramref name="subject"/> from this request's client, and says how long to wait when
    /// either budget is spent.
    /// </summary>
    /// <remarks>
    /// The ceiling is looked at first without spending it (a zero-permit acquire), so a client it refuses creates no
    /// subject partition. Then the subject, and the ceiling is spent only when the subject allowed the attempt: a flood
    /// for one address, once throttled, stops drawing on the ceiling, so it cannot use the client's shared budget up
    /// for its other addresses.
    /// </remarks>
    /// <param name="http">The request, whose antiforgery token has already been validated.</param>
    /// <param name="subject">The subject, from <see cref="SignInSubject"/> or <see cref="SetPasswordSubject"/>.</param>
    /// <returns><see langword="null"/> when the attempt may proceed; otherwise how long to wait.</returns>
    internal TimeSpan? Charge(HttpContext http, string subject)
    {
        var client = ClientOf(http.Connection.RemoteIpAddress);

        using (var ceiling = _clients.AttemptAcquire(client, 0))
        {
            if (!ceiling.IsAcquired)
            {
                return RetryAfter(ceiling);
            }
        }

        using var perSubject = _subjects.AttemptAcquire($"{client}|{subject}");
        if (!perSubject.IsAcquired)
        {
            return RetryAfter(perSubject);
        }

        using var perClient = _clients.AttemptAcquire(client);
        return perClient.IsAcquired ? null : RetryAfter(perClient);
    }

    /// <summary>Answers a throttled post: back to <paramref name="location"/>, with <c>Retry-After</c>.</summary>
    /// <remarks>
    /// A redirect, never a bare <c>429</c>: a browser shows a form post's error status as a blank page, and the person
    /// has no idea that waiting a minute would work.
    /// </remarks>
    /// <param name="http">The request.</param>
    /// <param name="wait">How long to wait.</param>
    /// <param name="location">The page, with <c>throttled=true</c> in its query.</param>
    internal static IResult Throttled(HttpContext http, TimeSpan wait, string location)
    {
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return AlvoAdminRedirect.SeeOther(http, location);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _subjects.Dispose();
        _clients.Dispose();
    }

    private static PartitionedRateLimiter<string> FixedWindows(int perMinute, Action created)
        => PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ =>
            {
                created();
                return new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(perMinute, 1),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                };
            }));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    private static TimeSpan RetryAfter(RateLimitLease lease)
        => lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromMinutes(1);

    /// <summary>The refusal for a count that is not a whole number above zero.</summary>
    private static string NotPositive(string key, string? configured, int fallback)
    {
        var variable = key.Replace(":", "__", StringComparison.Ordinal);
        return string.Join(
            Environment.NewLine,
            $"Alvo cannot start: {variable} is '{configured}', and it has to be a whole number above zero.",
            string.Empty,
            string.Create(CultureInfo.InvariantCulture, $"  Set:        {variable}={fallback}, or unset it for that default."));
    }
}

/// <summary>The credential limit's two numbers, read from configuration and validated at start.</summary>
internal sealed class AlvoAdminCredentialLimitOptions
{
    /// <summary>The design's budget per subject per minute.</summary>
    internal const int DefaultAttempts = 20;

    /// <summary>The per-client ceiling per minute across both forms.</summary>
    internal const int DefaultCeiling = 200;

    /// <summary>How many attempts one client may make at one subject per minute.</summary>
    public int AttemptsPerMinute { get; set; } = DefaultAttempts;

    /// <summary>How many credential posts one client may make per minute, across every subject and both forms.</summary>
    public int CeilingPerMinute { get; set; } = DefaultCeiling;

    /// <summary>Takes the configured text, where anything that is not a whole number reads as zero and is refused.</summary>
    /// <param name="attempts">The configured attempts, or <see langword="null"/>.</param>
    /// <param name="ceiling">The configured ceiling, or <see langword="null"/>.</param>
    internal void Read(string? attempts, string? ceiling)
    {
        AttemptsPerMinute = Parse(attempts, DefaultAttempts);
        CeilingPerMinute = Parse(ceiling, DefaultCeiling);
    }

    private static int Parse(string? configured, int fallback)
        => configured is null ? fallback
            : int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
