namespace MMLib.Alvo.Auth;

/// <summary>The provider port over persisted API keys.</summary>
public interface IApiKeyStore
{
    /// <summary>Finds an API key record by its public identifier.</summary>
    /// <param name="keyId">The key's public identifier.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <returns>The record when it exists, or <see langword="null"/> when no such key is stored.</returns>
    ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken);

    /// <summary>Records that an API key was just used.</summary>
    /// <param name="keyId">The key's public identifier.</param>
    /// <param name="usedAt">The instant the key was used.</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken cancellationToken);
}
