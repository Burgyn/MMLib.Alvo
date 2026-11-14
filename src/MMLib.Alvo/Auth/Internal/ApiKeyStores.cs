using System.Collections.Concurrent;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// The <see cref="IApiKeyStore"/> the resolver reads: a routing store that sends every <c>alvo_pat_</c>/<c>alvo_svc_</c>
/// key id to the persisted app-token registry and every other one to the configured dev keys (S5 plan D4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The prefix decides, never a fallback.</b> The prefixes are reserved (<see cref="AlvoAuthOptionsValidator"/>), so a
/// prefixed id is never looked up among the dev keys and an unprefixed one never in the registry — one lookup per
/// request, and no configured key can shadow a minted one. Without a registry an app token is unknown, like any unknown id.
/// </para>
/// <para>
/// <b>App-token touches are throttled</b> to one per <see cref="TouchInterval"/> per key on this replica: <c>last_used_at</c>
/// is a hint for the owner's listing, not an audit trail, and a write per request would turn every read into a write. A dev
/// key's touch is in memory and stays unthrottled.
/// </para>
/// </remarks>
/// <param name="devKeys">The configured dev keys.</param>
/// <param name="appTokens">The persisted app-token registry, or <see langword="null"/> where none is registered.</param>
/// <param name="clock">The clock the touch throttle reads.</param>
internal sealed class ApiKeyStores(InMemoryApiKeyStore devKeys, IAppTokenStore? appTokens, TimeProvider clock) : IApiKeyStore
{
    /// <summary>The shortest gap between two persisted touches of one app token from this replica.</summary>
    internal static TimeSpan TouchInterval { get; } = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTouched = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        if (!AppTokenSecret.IsAppTokenId(keyId))
        {
            return devKeys.FindAsync(keyId, cancellationToken);
        }

        return appTokens is null ? ValueTask.FromResult<ApiKeyRecord?>(null) : appTokens.FindAsync(keyId, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        if (!AppTokenSecret.IsAppTokenId(keyId))
        {
            return devKeys.TouchAsync(keyId, usedAt, cancellationToken);
        }

        return appTokens is not null && ClaimTouch(keyId)
            ? appTokens.TouchAsync(keyId, usedAt, cancellationToken)
            : ValueTask.CompletedTask;
    }

    /// <summary>Whether this replica's last touch of <paramref name="keyId"/> is at least <see cref="TouchInterval"/> old.</summary>
    private bool ClaimTouch(string keyId)
    {
        var now = clock.GetUtcNow();
        var claimed = false;
        _lastTouched.AddOrUpdate(
            keyId,
            _ =>
            {
                claimed = true;
                return now;
            },
            (_, last) =>
            {
                claimed = now - last >= TouchInterval;
                return claimed ? now : last;
            });
        return claimed;
    }
}
