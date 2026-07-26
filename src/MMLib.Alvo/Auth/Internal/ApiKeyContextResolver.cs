using MMLib.Alvo.Rules;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// Resolves the framework's built-in dev API-key credential (<c>"&lt;keyId&gt;.&lt;secret&gt;"</c>)
/// into an <see cref="AlvoPrincipal"/>. Every failure path returns <see langword="null"/>: an
/// absent, malformed, or wrong credential, an unknown key id, an unusable (expired/revoked) key,
/// an undeclared role name, and a tenant mismatch are all indistinguishable to the caller. An
/// unknown key id still runs a hash comparison against a same-length dummy hash, so the response
/// time does not reveal whether the key id exists.
/// </summary>
/// <remarks>
/// A role name on a key is resolved against the <b>applied project's</b> declared roles — the
/// <see cref="RoleCatalog"/> the primed <see cref="PolicyCatalog"/> carries — so the descriptor's
/// <c>auth.roles</c> governs both halves of authorization from one declaration, and adding or
/// removing a role takes effect on the very next request. The injected <see cref="RoleCatalog"/>
/// serves only until a project is applied, when there is no descriptor to read roles from; it holds
/// the built-ins alone unless a host replaced the registration, so an unprimed host refuses an
/// application role rather than minting one nothing has declared.
/// </remarks>
internal sealed class ApiKeyContextResolver(
    IApiKeyStore store,
    RoleCatalog roleCatalog,
    TimeProvider clock,
    TenantResolver tenantResolver,
    IPolicyCatalogProvider policyCatalogProvider)
    : IAlvoContextResolver
{
    private const char KeySeparator = '.';

    private static readonly string _dummyHash = ApiKeyHash.Compute(Guid.NewGuid().ToString());

    /// <summary>
    /// The <see cref="TenantResolver"/> this instance was constructed with — exposed internally
    /// so a test can prove the DI-registered singleton, not a hard-wired instance, is what
    /// authentication actually consults.
    /// </summary>
    internal TenantResolver TenantResolver => tenantResolver;

    /// <inheritdoc/>
    public async ValueTask<AlvoPrincipal?> ResolveAsync(
        string? presentedKey, string? requestedTenant, CancellationToken cancellationToken)
    {
        if (!TrySplitPresentedKey(presentedKey, out var keyId, out var secret))
        {
            return null;
        }

        var record = await store.FindAsync(keyId, cancellationToken).ConfigureAwait(false);
        if (!VerifySecret(record, secret))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (record is not { } usable || !usable.IsUsable(now))
        {
            return null;
        }

        if (!TryResolveRoles(usable.RoleNames, out var roles))
        {
            return null;
        }

        if (!tenantResolver.TryResolve(usable, requestedTenant, out var tenant))
        {
            return null;
        }

        await store.TouchAsync(keyId, now, cancellationToken).ConfigureAwait(false);
        return BuildPrincipal(usable, roles, tenant);
    }

    private static bool TrySplitPresentedKey(
        [NotNullWhen(true)] string? presented, out string keyId, out string secret)
    {
        keyId = string.Empty;
        secret = string.Empty;
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var separatorIndex = presented.IndexOf(KeySeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == presented.Length - 1)
        {
            return false;
        }

        keyId = presented[..separatorIndex];
        secret = presented[(separatorIndex + 1)..];
        return true;
    }

    private static bool VerifySecret(ApiKeyRecord? record, string secret)
    {
        var expectedHash = record?.Sha256Hash ?? _dummyHash;
        var hashMatches = ApiKeyHash.Matches(secret, expectedHash);
        return record is not null && hashMatches;
    }

    private RoleCatalog DeclaredRoles => policyCatalogProvider.Current?.Roles ?? roleCatalog;

    private bool TryResolveRoles(IReadOnlyList<string> roleNames, out IReadOnlySet<Role> roles)
    {
        var declaredRoles = DeclaredRoles;
        var resolved = new HashSet<Role>();
        foreach (var name in roleNames)
        {
            if (!declaredRoles.TryGet(name, out var role))
            {
                roles = resolved;
                return false;
            }

            resolved.Add(role);
        }

        roles = resolved;
        return resolved.Count > 0;
    }

    private static AlvoPrincipal BuildPrincipal(ApiKeyRecord record, IReadOnlySet<Role> roles, TenantId? tenant) =>
        new()
        {
            Context = new AlvoContext
            {
                User = record.User,
                Roles = roles,
                Tenant = tenant,
            },
            Scopes = record.Scopes,
            KeyId = record.KeyId,
        };
}
