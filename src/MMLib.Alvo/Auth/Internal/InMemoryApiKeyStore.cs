using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// The framework's built-in <see cref="IApiKeyStore"/>, backed entirely by
/// <see cref="AlvoAuthOptions.DevKeys"/> — configuration, never a database. Every
/// <see cref="AlvoDevApiKey"/> is hashed and mapped into an <see cref="ApiKeyRecord"/> once, at
/// construction; an entry whose scopes fail <see cref="ApiKeyScope.TryParse"/> is skipped
/// entirely rather than partially trusted.
/// </summary>
internal sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly Dictionary<string, ApiKeyRecord> _records;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsedAt = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of <see cref="InMemoryApiKeyStore"/> from configured dev keys.</summary>
    /// <param name="options">The configured dev API keys.</param>
    public InMemoryApiKeyStore(IOptions<AlvoAuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _records = BuildRecords(options.Value.DevKeys);
    }

    /// <inheritdoc/>
    public ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken)
    {
        if (!_records.TryGetValue(keyId, out var record))
        {
            return ValueTask.FromResult<ApiKeyRecord?>(null);
        }

        var lastUsedAt = _lastUsedAt.TryGetValue(keyId, out var touchedAt) ? touchedAt : record.LastUsedAt;
        return ValueTask.FromResult<ApiKeyRecord?>(record with { LastUsedAt = lastUsedAt });
    }

    /// <inheritdoc/>
    public ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken cancellationToken)
    {
        _lastUsedAt[keyId] = usedAt;
        return ValueTask.CompletedTask;
    }

    private static Dictionary<string, ApiKeyRecord> BuildRecords(IEnumerable<AlvoDevApiKey> devKeys) =>
        devKeys
            .Select(TryBuildRecord)
            .Where(record => record is not null)
            .ToDictionary(record => record!.KeyId, record => record!, StringComparer.Ordinal);

    private static ApiKeyRecord? TryBuildRecord(AlvoDevApiKey key)
    {
        if (!TryParseScopes(key.Scopes, out var scopes))
        {
            return null;
        }

        return new ApiKeyRecord
        {
            KeyId = key.KeyId,
            Sha256Hash = ApiKeyHash.Compute(key.Secret),
            User = new UserId(key.User),
            RoleNames = key.Roles.ToList(),
            Tenant = key.Tenant is { } tenant ? new TenantId(tenant) : null,
            Scopes = scopes,
            ExpiresAt = key.ExpiresAt,
        };
    }

    private static bool TryParseScopes(IEnumerable<string> scopeTexts, out IReadOnlySet<ApiKeyScope> scopes)
    {
        var parsed = new HashSet<ApiKeyScope>();
        foreach (var text in scopeTexts)
        {
            if (!ApiKeyScope.TryParse(text, out var scope))
            {
                scopes = parsed;
                return false;
            }

            parsed.Add(scope);
        }

        scopes = parsed;
        return true;
    }
}
