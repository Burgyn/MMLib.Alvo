using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Management;

/// <summary>Registers the Management API's options and its authorization gate.</summary>
/// <remarks>
/// Called from <c>AddAlvo</c> and nowhere else, exactly as <c>AddAlvoApi</c> is: registering a service
/// exposes nothing, and the endpoint seam is <c>MapAlvoManagementApi()</c>
/// (<c>docs/architecture/extensibility.md</c> rule 10).
/// </remarks>
internal static class ManagementSetup
{
    /// <summary>Adds <see cref="AlvoManagementOptions"/> and <see cref="ManagementAccessEvaluator"/>.</summary>
    /// <param name="services">The service collection to add the management services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoManagement(this IServiceCollection services)
    {
        AddManagementOptions(services);
        services.TryAddSingleton<ManagementAccessEvaluator>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="AlvoManagementOptions"/>, bound from its configuration section and validated at
    /// startup.
    /// </summary>
    /// <remarks>
    /// The binder's <see cref="IConfiguration"/> arrives through a factory rather than as a constructor
    /// dependency, on <c>AddSchemaOptions</c>'s precedent: a nullable constructor parameter is not an
    /// optional dependency to the container, so a host that registered no configuration would fail to
    /// activate the binder at all.
    /// </remarks>
    /// <param name="services">The service collection to add the options to.</param>
    private static void AddManagementOptions(IServiceCollection services)
    {
        services.AddOptions<AlvoManagementOptions>().ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<AlvoManagementOptions>, AlvoManagementOptionsConfiguration>(
                Create));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoManagementOptions>, AlvoManagementOptionsConfiguration>(
                Create));

        static AlvoManagementOptionsConfiguration Create(IServiceProvider provider) =>
            new(provider.GetService<IConfiguration>(), provider.GetRequiredService<IOptions<Api.AlvoApiOptions>>());
    }
}
