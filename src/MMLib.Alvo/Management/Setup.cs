using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Management;

/// <summary>Registers the Management API's authorization gate.</summary>
internal static class ManagementSetup
{
    /// <summary>Adds <see cref="ManagementAccessEvaluator"/>.</summary>
    /// <param name="services">The service collection to add the management services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoManagement(this IServiceCollection services)
    {
        services.TryAddSingleton<ManagementAccessEvaluator>();
        return services;
    }
}
