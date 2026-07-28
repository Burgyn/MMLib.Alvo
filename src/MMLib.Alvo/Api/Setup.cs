using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Api;

/// <summary>Registers the generated Data API's services: its options, its route catalog, and its authorization filter.</summary>
internal static class ApiSetup
{
    /// <summary>
    /// Adds <see cref="AlvoApiOptions"/> (validated at startup by
    /// <see cref="AlvoApiOptionsValidator"/>), the <see cref="EntityRouteCatalog"/> that answers which
    /// entities get routes, and the <see cref="AlvoContextFilterFactory"/> that builds one
    /// <see cref="AlvoContextFilter"/> per mapped endpoint.
    /// </summary>
    /// <remarks>
    /// Called from <c>AddAlvo</c> rather than only from <c>AddDataApi</c>, for the same reason
    /// <c>AddAlvoAuth</c>/<c>AddAlvoRules</c> are: registering a service exposes nothing. Nothing here
    /// is reachable until a host calls <c>MapAlvoDataApi</c>, which is a separate seam by design
    /// (<c>docs/architecture/extensibility.md</c> rule 10) — so <c>AddDataApi</c> exists to
    /// <em>configure</em> the feature and to make it discoverable, not to switch it on.
    /// <c>TryAdd*</c> throughout, so a host can substitute any of the three; the validator is added
    /// with <c>TryAddEnumerable</c>, exactly as <c>AddAlvoAuth</c> adds its own, so registering
    /// <c>AddAlvo</c> twice does not report every failure twice.
    /// </remarks>
    /// <param name="services">The service collection to add the API services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoApi(this IServiceCollection services)
    {
        services.AddOptions<AlvoApiOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoApiOptions>, AlvoApiOptionsValidator>());
        services.TryAddSingleton<EntityRouteCatalog>();
        services.TryAddSingleton<AlvoContextFilterFactory>();
        return services;
    }
}
