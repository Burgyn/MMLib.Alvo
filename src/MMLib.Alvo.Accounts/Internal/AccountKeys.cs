using Microsoft.IdentityModel.Tokens;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>An ES256 key access tokens are signed or validated with.</summary>
/// <param name="Kid">The key's id: its RFC 7638 JWK thumbprint.</param>
/// <param name="Key">The key; carries the private half, so it is never printed.</param>
internal sealed record SigningKey(string Kid, ECDsaSecurityKey Key)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Kid = ").Append(Kid);
        return true;
    }
}

/// <summary>
/// The deployment's signing and refresh-MAC keys (S2 D12): generation 1 of each purpose is created on first use through
/// the store's insert-if-absent, so every replica converges on the one written first, then held in memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>No I/O on a known <c>kid</c>.</b> The signing keys are read once; a token naming a <c>kid</c> this replica has not
/// seen reloads them from the store, at most once per <see cref="ReloadInterval"/>, so a flood of invented <c>kid</c>s
/// costs one read per interval, not one per request.
/// </para>
/// <para>
/// <b>The first load also ensures a signing key exists</b>, even on a replica that only validates: the key it then knows is
/// the one every replica converges on, so a token another replica mints later names a <c>kid</c> already held.
/// </para>
/// </remarks>
/// <param name="store">The account store, which seals every key's material.</param>
/// <param name="clock">The clock judging retirement and the reload interval.</param>
internal sealed class AccountKeys(IAccountStore store, TimeProvider clock) : IDisposable
{
    /// <summary>How often a <c>kid</c> this replica does not know may reload the signing keys.</summary>
    internal static TimeSpan ReloadInterval { get; } = TimeSpan.FromMinutes(5);

    private const int Generation = 1;

    private const int RefreshKeyBytes = 32;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile KeySet? _keys;

    private volatile byte[]? _refreshKey;

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    /// <summary>The key new access tokens are signed with.</summary>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<SigningKey> SigningAsync(CancellationToken ct) => (await KeysAsync(ct).ConfigureAwait(false)).Signing;

    /// <summary>The HMAC key refresh-token values are derived with.</summary>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<byte[]> RefreshMacKeyAsync(CancellationToken ct)
    {
        if (_refreshKey is { } cached)
        {
            return cached;
        }

        var candidate = new KeyCandidate(
            AccountKeyPurposes.Refresh,
            Generation,
            Guid.NewGuid().ToString("N"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(RefreshKeyBytes)),
            clock.GetUtcNow());
        var stored = await store.EnsureKeyAsync(candidate, ct).ConfigureAwait(false);
        return _refreshKey = Convert.FromBase64String(stored.Material);
    }

    /// <summary>Every signing key not retired, as the JWKS publishes them.</summary>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<IReadOnlyList<SigningKey>> PublishedAsync(CancellationToken ct)
        => [.. (await KeysAsync(ct).ConfigureAwait(false)).ByKid.Values];

    /// <summary>The signing key <paramref name="kid"/>, or <see langword="null"/> when the store has no such live key.</summary>
    /// <param name="kid">The <c>kid</c> a token names.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async ValueTask<SigningKey?> FindAsync(string kid, CancellationToken ct)
    {
        var keys = await KeysAsync(ct).ConfigureAwait(false);
        if (keys.ByKid.TryGetValue(kid, out var known))
        {
            return known;
        }

        keys = await ReloadAsync(keys, ct).ConfigureAwait(false);
        return keys.ByKid.GetValueOrDefault(kid);
    }

    private async ValueTask<KeySet> KeysAsync(CancellationToken ct)
    {
        if (_keys is { } loaded)
        {
            return loaded;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _keys ??= await LoadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<KeySet> ReloadAsync(KeySet seen, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _keys!;
            if (!ReferenceEquals(current, seen) || clock.GetUtcNow() - current.ReadAt < ReloadInterval)
            {
                return current;
            }

            return _keys = await ReadAsync(current.Signing, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<KeySet> LoadAsync(CancellationToken ct)
    {
        var stored = await store.EnsureKeyAsync(NewSigningCandidate(), ct).ConfigureAwait(false);
        return await ReadAsync(ToSigningKey(stored), ct).ConfigureAwait(false);
    }

    private async Task<KeySet> ReadAsync(SigningKey signing, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var stored = await store.ReadKeysAsync(AccountKeyPurposes.Signing, now, ct).ConfigureAwait(false);
        var byKid = stored.ToDictionary(key => key.Kid, ToSigningKey, StringComparer.Ordinal);
        byKid.TryAdd(signing.Kid, signing);
        return new KeySet(signing, byKid, now);
    }

    private KeyCandidate NewSigningCandidate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var material = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        return new KeyCandidate(AccountKeyPurposes.Signing, Generation, Thumbprint(key), material, clock.GetUtcNow());
    }

    private static SigningKey ToSigningKey(StoredKey stored)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.Material), out _);
        return new SigningKey(stored.Kid, new ECDsaSecurityKey(key) { KeyId = stored.Kid });
    }

    /// <summary>The RFC 7638 thumbprint of <paramref name="key"/>'s public half, base64url.</summary>
    /// <param name="key">A P-256 key.</param>
    internal static string Thumbprint(ECDsa key)
    {
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(key));
        return Base64Url.EncodeToString(jwk.ComputeJwkThumbprint());
    }

    private sealed record KeySet(SigningKey Signing, IReadOnlyDictionary<string, SigningKey> ByKid, DateTimeOffset ReadAt);
}
