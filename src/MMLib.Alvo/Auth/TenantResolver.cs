using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Auth;

/// <summary>
/// Resolves which tenant a caller acts in from the API key and the tenant the caller requested.
/// The key's own tenant always wins: a caller cannot widen a key's scope by requesting a
/// different tenant. A key with no tenant and no request resolves to <see langword="null"/>
/// with a successful result — that denial belongs to the policy engine, which is the one that
/// knows whether the target entity is tenant-scoped.
/// </summary>
public sealed class TenantResolver
{
    /// <summary>Resolves the effective tenant for a request.</summary>
    /// <param name="key">The API key record the caller authenticated with.</param>
    /// <param name="requestedTenant">The tenant the caller asked to act in, if any.</param>
    /// <param name="tenant">
    /// The resolved tenant, when this method returns <see langword="true"/>; <see langword="null"/>
    /// on denial and when the key has no tenant and none was requested.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when <paramref name="requestedTenant"/> is malformed or differs
    /// from the key's own tenant.
    /// </returns>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Registered and consumed as a DI singleton like the other auth services; kept an instance member for that parity.")]
    public bool TryResolve(ApiKeyRecord key, string? requestedTenant, out TenantId? tenant)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (string.IsNullOrEmpty(requestedTenant))
        {
            tenant = key.Tenant;
            return true;
        }

        return TryResolveRequested(key.Tenant, requestedTenant, out tenant);
    }

    private static bool TryResolveRequested(TenantId? keyTenant, string requestedTenant, out TenantId? tenant)
    {
        if (!TenantId.TryParse(requestedTenant, provider: null, out var parsedRequested))
        {
            tenant = null;
            return false;
        }

        if (keyTenant is { } ownedTenant && ownedTenant != parsedRequested)
        {
            tenant = null;
            return false;
        }

        tenant = parsedRequested;
        return true;
    }
}
