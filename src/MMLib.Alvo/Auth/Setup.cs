using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Auth;

/// <summary>Registers the dev API-key auth mechanism: key resolution, scope gating, and tenant resolution.</summary>
internal static class AuthSetup
{
    /// <summary>
    /// Adds the dev API-key <see cref="IAlvoContextResolver"/>, <see cref="ScopeGate"/> and
    /// <see cref="TenantResolver"/>, plus a <see cref="RoleCatalog"/> holding only the built-in
    /// roles until the descriptor pipeline (Task 13) replaces it. <see cref="AlvoAuthOptions"/>
    /// fails fast at startup (<see cref="Internal.AlvoAuthOptionsValidator"/>) on a misconfigured
    /// dev key, rather than silently dropping it.
    /// </summary>
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
        services.TryAddSingleton<ScopeGate>();
        services.TryAddSingleton<TenantResolver>();
        return services;
    }
}
