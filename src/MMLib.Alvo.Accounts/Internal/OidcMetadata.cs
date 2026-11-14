using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Text.Json;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// One provider's discovery document and signing keys, fetched on first use, re-fetched when stale, and re-fetched on a
/// <c>kid</c> it does not publish at most once per <see cref="UnknownKidInterval"/> (spec §5.2 step 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deviation from IdentityModel's <c>ConfigurationManager</c>.</b> Its 8.x line refreshes in the background once a
/// configuration exists (its own documentation; only the process-wide AppContext switch
/// <c>Switch.Microsoft.IdentityModel.UpdateConfigAsBlocking</c> makes it block), so the first ID token signed with a
/// key a provider just rotated in would be refused, and its clock is not the injected <see cref="TimeProvider"/>. This
/// cache blocks the one request that finds the unknown <c>kid</c>, and judges both intervals on Alvo's clock. The
/// fetch itself is IdentityModel's: <see cref="OpenIdConnectConfigurationRetriever"/> over an
/// <see cref="HttpDocumentRetriever"/> that refuses plain HTTP unless the provider allows it.
/// </para>
/// <para>
/// <b>A failed re-fetch keeps the keys already held</b>, as <c>ConfigurationManager</c> does, and is retried after
/// <see cref="UnknownKidInterval"/> rather than on every request; only a provider never reached is
/// <see cref="IdTokenRefusal.Unreachable"/>.
/// </para>
/// <para>
/// <b>A provider never reached is not asked again on every attempt.</b> A failed first fetch backs off
/// <see cref="FirstBackoff"/>, doubling per failure up to <see cref="MaxBackoff"/>, and every attempt inside the backoff
/// is <see cref="IdTokenRefusal.Unreachable"/> with no request. The backoff is re-read after the gate is acquired, so the
/// attempts queued behind the one that failed answer at once rather than each fetching again — an unsigned JWT naming
/// the provider's issuer is all a caller needs to reach this point, so without it every attempt would be an outbound
/// request held for the client's whole timeout.
/// </para>
/// </remarks>
/// <param name="provider">The provider.</param>
/// <param name="clients">Where the <see cref="OidcIdTokenVerifier.HttpClientName"/> client comes from.</param>
/// <param name="clock">The clock both intervals are judged on.</param>
/// <param name="logger">Where a failed fetch is reported — the provider and its authority, never a token.</param>
internal sealed partial class OidcMetadata(
    OidcProvider provider, IHttpClientFactory clients, TimeProvider clock, ILogger logger) : IDisposable
{
    /// <summary>Gets how often a <c>kid</c> the provider does not publish may re-fetch its keys.</summary>
    internal static TimeSpan UnknownKidInterval { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets how long fetched keys are used before they are fetched again (<c>ConfigurationManager</c>'s default).</summary>
    internal static TimeSpan RefreshInterval { get; } = TimeSpan.FromHours(12);

    /// <summary>Gets how long a provider never reached is left alone after its first failed fetch.</summary>
    internal static TimeSpan FirstBackoff { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the longest a provider never reached is left alone after a failed fetch.</summary>
    internal static TimeSpan MaxBackoff { get; } = TimeSpan.FromMinutes(5);

    private const string DiscoveryPath = "/.well-known/openid-configuration";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile Snapshot? _current;

    private DateTimeOffset? _lastUnknownKidFetch;

    private volatile Backoff? _backoff;

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    /// <summary>The provider's configuration, or <see langword="null"/> when it has never been reached.</summary>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<OpenIdConnectConfiguration?> GetAsync(CancellationToken ct)
    {
        if (TryHeld(out var held))
        {
            return held;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return TryHeld(out var fetched) ? fetched : (await FetchAsync(ct).ConfigureAwait(false))?.Configuration;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-fetches the keys because a token names a <c>kid</c> <paramref name="seen"/> does not publish — unless another
    /// request already did, or one did within <see cref="UnknownKidInterval"/>.
    /// </summary>
    /// <param name="seen">The configuration the caller looked the <c>kid</c> up in.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<OpenIdConnectConfiguration> RefreshForUnknownKidAsync(OpenIdConnectConfiguration seen, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (!ReferenceEquals(_current?.Configuration, seen) || now - _lastUnknownKidFetch < UnknownKidInterval)
            {
                return _current?.Configuration ?? seen;
            }

            _lastUnknownKidFetch = now;
            return (await FetchAsync(ct).ConfigureAwait(false))?.Configuration ?? seen;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryHeld(out OpenIdConnectConfiguration? configuration)
    {
        var now = clock.GetUtcNow();
        if (_current is { } current && now < current.RefreshAt)
        {
            configuration = current.Configuration;
            return true;
        }

        configuration = null;
        return _current is null && _backoff is { } backoff && now < backoff.RetryAt;
    }

    private async Task<Snapshot?> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var client = clients.CreateClient(OidcIdTokenVerifier.HttpClientName);
            var retriever = new HttpDocumentRetriever(client) { RequireHttps = !provider.AllowHttpMetadata };
            var configuration = await OpenIdConnectConfigurationRetriever
                .GetAsync(provider.Authority.TrimEnd('/') + DiscoveryPath, retriever, ct)
                .ConfigureAwait(false);
            _backoff = null;
            return _current = new Snapshot(configuration, clock.GetUtcNow() + RefreshInterval);
        }
        catch (Exception failure) when (IsFetchFailure(failure, ct))
        {
            MetadataUnreachable(logger, provider.Name, provider.Authority, failure.GetType().Name);
            return Failed();
        }
    }

    private Snapshot? Failed()
    {
        var now = clock.GetUtcNow();
        if (_current is { } held)
        {
            return _current = held with { RefreshAt = now + UnknownKidInterval };
        }

        var delay = _backoff is { } previous ? Min(previous.Delay * 2, MaxBackoff) : FirstBackoff;
        _backoff = new Backoff(now + delay, delay);
        return null;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static bool IsFetchFailure(Exception failure, CancellationToken ct)
        => failure is IOException or HttpRequestException or ArgumentException or InvalidOperationException or JsonException
            || (failure is OperationCanceledException && !ct.IsCancellationRequested);

    [LoggerMessage(
        EventId = 6402,
        Level = LogLevel.Warning,
        Message = "Identity provider {Provider}'s discovery document or signing keys at {Authority} could not be "
            + "fetched ({Failure}); the keys already held, if any, stay in use until a fetch succeeds.")]
    private static partial void MetadataUnreachable(ILogger logger, string provider, string authority, string failure);

    private sealed record Snapshot(OpenIdConnectConfiguration Configuration, DateTimeOffset RefreshAt);

    private sealed record Backoff(DateTimeOffset RetryAt, TimeSpan Delay);
}
