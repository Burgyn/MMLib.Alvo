using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Auth;

/// <summary>Registers the dev API-key auth mechanism: key resolution, the ambient caller, scope gating, and tenant resolution.</summary>
internal static class AuthSetup
{
    /// <summary>
    /// Adds the dev API-key <see cref="IAlvoContextResolver"/>, the ambient
    /// <see cref="IAlvoContextAccessor"/>, <see cref="ScopeGate"/> and
    /// <see cref="TenantResolver"/>, plus the pre-apply <see cref="RoleCatalog"/>.
    /// <see cref="AlvoAuthOptions"/> fails fast at startup
    /// (<see cref="Internal.AlvoAuthOptionsValidator"/>) on a misconfigured dev key, rather than
    /// silently dropping it.
    /// </summary>
    /// <remarks>
    /// The registered <see cref="RoleCatalog"/> holds the built-in roles only, and is consulted
    /// solely while <see cref="IRoleCatalogProvider.DeclaredRoles"/> has nothing to declare — by
    /// default, until a project is applied, after which the applied descriptor's <c>auth.roles</c>
    /// is authoritative (see <see cref="Internal.ApiKeyContextResolver"/>). It is registered with
    /// <c>TryAddSingleton</c> so a host with no descriptor at all — no <c>AddAlvo</c> descriptor
    /// pipeline, roles configured in code — can still declare its roles by registering its own; a
    /// host that later <em>does</em> apply a descriptor is superseded by it, and a host that wants
    /// to stay authoritative registers an <see cref="IRoleCatalogProvider"/> instead.
    /// </remarks>
    /// <param name="services">The service collection to add the auth services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoAuth(this IServiceCollection services)
    {
        services.AddOptions<AlvoAuthOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoAuthOptions>, Internal.AlvoAuthOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(RoleCatalog.BuiltInOnly);
        services.TryAddSingleton<IApiKeyStore, Internal.InMemoryApiKeyStore>();
        services.TryAddSingleton<IAlvoContextResolver, Internal.ApiKeyContextResolver>();
        services.TryAddSingleton<IAlvoContextAccessor, Internal.AlvoContextAccessor>();
        services.TryAddSingleton<ScopeGate>();
        services.TryAddSingleton<TenantResolver>();
        return services;
    }
}
