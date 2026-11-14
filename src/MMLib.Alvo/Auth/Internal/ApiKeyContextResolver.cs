using MMLib.Alvo.Rules;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// Resolves the framework's built-in dev API-key credential (<c>"&lt;keyId&gt;.&lt;secret&gt;"</c>)
/// into an <see cref="AlvoPrincipal"/>. Every failure path returns <see langword="null"/>: an
/// absent, malformed, or wrong credential, an unknown key id, an unusable (expired/revoked) key,
/// an undeclared role name, and a tenant mismatch are all indistinguishable to the caller — and so, for an app token
/// (<c>alvo_pat_</c>/<c>alvo_svc_</c>), are a kind the applied descriptor does not turn on and a membership that no longer
/// answers (<see cref="AppTokenPrincipals"/>). An
/// unknown key id still runs a hash comparison against a same-length dummy hash, so the response
/// time does not reveal whether the key id exists.
/// </summary>
/// <remarks>
/// A role name on a key is resolved against <see cref="IRoleCatalogProvider.DeclaredRoles"/> — by
/// default the <b>applied project's</b> <c>auth.roles</c>, since the policy catalog provider
/// implements that port, so one declaration governs both halves of authorization and adding or
/// removing a role takes effect on the very next request. Authentication depends on the role-shaped
/// port and not on the policy catalog, so an external identity source can supply roles instead
/// without this type changing. The injected <see cref="RoleCatalog"/> serves only while the provider
/// has nothing to declare; it holds the built-ins alone unless a host replaced the registration, so
/// an unprimed host refuses an application role rather than minting one nothing has declared.
/// </remarks>
internal sealed class ApiKeyContextResolver(
    IApiKeyStore store,
    RoleCatalog roleCatalog,
    TimeProvider clock,
    TenantResolver tenantResolver,
    IRoleCatalogProvider roleCatalogProvider,
    AppTokenPrincipals appTokens)
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

        if (usable.Kind is not ApiKeyKind.Configured)
        {
            return await ResolveAppTokenAsync(usable, requestedTenant, now, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// An app token (S5 plan D5, D6): its tenant confirmed like any key's, then the principal its kind acts as, then the
    /// (throttled) touch — every refusal before the touch, so a refused token records no use.
    /// </summary>
    private async ValueTask<AlvoPrincipal?> ResolveAppTokenAsync(
        ApiKeyRecord token, string? requestedTenant, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!tenantResolver.TryResolve(token, requestedTenant, out var tenant))
        {
            return null;
        }

        var principal = await appTokens.ResolveAsync(token, tenant, cancellationToken).ConfigureAwait(false);
        if (principal is not null)
        {
            await store.TouchAsync(token.KeyId, now, cancellationToken).ConfigureAwait(false);
        }

        return principal;
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

    private RoleCatalog DeclaredRoles => roleCatalogProvider.DeclaredRoles ?? roleCatalog;

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
                ActorKind = AlvoActorKind.ApiKey,
            },
            Scopes = record.Scopes,
            KeyId = record.KeyId,
        };
}
