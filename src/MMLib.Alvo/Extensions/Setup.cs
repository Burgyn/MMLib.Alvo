using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Extensions;

/// <summary>
/// Registers the validated <see cref="AlvoExtensionOptions"/> and <see cref="AlvoDeliveryOptions"/>, the depth limit
/// every store resolves, the <see cref="ExtensionStateGuard"/> the boot runs before it reports ready, and the
/// <see cref="ExtensionCatalog"/> that describes the host's registrations, and the <see cref="ExtensionStats"/> that count
/// their runs on this instance — a hosted service too, so it listens from the host's start and stops with it.
/// </summary>
internal static class ExtensionsSetup
{
    internal static IServiceCollection AddAlvoExtensions(this IServiceCollection services)
    {
        services.AddOptions<AlvoExtensionOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<AlvoExtensionOptions>, AlvoExtensionOptionsConfiguration>(Create));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoExtensionOptions>, AlvoExtensionOptionsConfiguration>(Create));
        services.AddOptions<AlvoDeliveryOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<AlvoDeliveryOptions>, AlvoDeliveryOptionsConfiguration>(Deliveries));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoDeliveryOptions>, AlvoDeliveryOptionsConfiguration>(Deliveries));
        services.TryAddSingleton(DepthLimit);
        services.TryAddSingleton<IAlvoSessions, AlvoSessions>();
        services.TryAddSingleton(provider => new ExtensionRegistry(provider.GetServices<ExtensionRegistration>()));
        services.TryAddSingleton<ExtensionBinder>();
        services.TryAddSingleton<ExtensionCatalog>();
        services.TryAddSingleton<ExtensionApplyGuard>();
        services.TryAddSingleton<ExtensionStateGuard>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new ExtensionStats(provider.GetRequiredService<TimeProvider>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ExtensionStats>(provider => provider.GetRequiredService<ExtensionStats>()));

        return services;

        static AlvoExtensionOptionsConfiguration Create(IServiceProvider provider) =>
            new(provider.GetService<IConfiguration>());

        static AlvoDeliveryOptionsConfiguration Deliveries(IServiceProvider provider) =>
            new(provider.GetService<IConfiguration>());

        static AlvoChainDepthLimit DepthLimit(IServiceProvider provider) =>
            new(provider.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value.MaxDepth);
    }
}
