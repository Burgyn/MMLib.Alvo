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
/// <para>
/// <b>It answers one reserved id itself, so the token routes can ask whether a minted token would ever resolve</b> (Task 9
/// fix round 1). A host may replace this store (<see cref="IApiKeyStore"/> is a public port) or decorate it. The mint route
/// asks the <em>resolved</em> store for <see cref="ReachProbeKeyId"/>: a decorator that delegates passes
/// <see cref="ReachProbe"/> through, while a replacement — or this store with no registry — does not, and the route
/// refuses rather than showing a credential nothing will accept. The probe is answered without touching the registry, and
/// the record is dead as a credential: revoked, expired, and with a hash no secret's SHA-256 can equal.
/// </para>
/// </remarks>
/// <param name="devKeys">The configured dev keys.</param>
/// <param name="appTokens">The persisted app-token registry, or <see langword="null"/> where none is registered.</param>
internal sealed class ApiKeyStores(InMemoryApiKeyStore devKeys, IAppTokenStore? appTokens) : IApiKeyStore
{
    /// <summary>The shortest gap between two persisted touches of one app token from this replica.</summary>
    internal static TimeSpan TouchInterval { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The reserved id the mint route probes with. <c>~</c> is outside base64url, so no minted key id can ever equal it.
    /// </summary>
    internal const string ReachProbeKeyId = AppTokenSecret.PersonalPrefix + "~reach";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTouched = new(StringComparer.Ordinal);

    /// <summary>The record answered for <see cref="ReachProbeKeyId"/> while a registry is present; never a usable key.</summary>
    internal static ApiKeyRecord ReachProbe { get; } = new()
    {
        KeyId = ReachProbeKeyId,
        Sha256Hash = "~",
        User = new UserId(Guid.Empty),
        RoleNames = [],
        Scopes = new HashSet<ApiKeyScope>(),
        ExpiresAt = DateTimeOffset.MinValue,
        RevokedAt = DateTimeOffset.MinValue,
        Kind = ApiKeyKind.Personal,
    };

    /// <inheritdoc/>
    public ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        if (!AppTokenSecret.IsAppTokenId(keyId))
        {
            return devKeys.FindAsync(keyId, cancellationToken);
        }

        if (appTokens is null)
        {
            return ValueTask.FromResult<ApiKeyRecord?>(null);
        }

        return string.Equals(keyId, ReachProbeKeyId, StringComparison.Ordinal)
            ? ValueTask.FromResult<ApiKeyRecord?>(ReachProbe)
            : appTokens.FindAsync(keyId, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        if (!AppTokenSecret.IsAppTokenId(keyId))
        {
            return devKeys.TouchAsync(keyId, usedAt, cancellationToken);
        }

        return appTokens is not null && ClaimTouch(keyId, usedAt)
            ? appTokens.TouchAsync(keyId, usedAt, cancellationToken)
            : ValueTask.CompletedTask;
    }

    /// <summary>Whether this replica's last touch of <paramref name="keyId"/> is at least <see cref="TouchInterval"/> before <paramref name="now"/>.</summary>
    private bool ClaimTouch(string keyId, DateTimeOffset now)
    {
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
