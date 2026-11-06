using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Lifecycle;

/// <summary>Registers the data path's view of the descriptor's <c>stateMachine</c> and <c>timers</c>.</summary>
internal static class LifecycleSetup
{
    /// <summary>Adds <see cref="IEntityLifecycle"/>; the interface is internal, so no host can supply its own.</summary>
    /// <param name="services">The service collection.</param>
    internal static IServiceCollection AddAlvoLifecycle(this IServiceCollection services)
    {
        services.TryAddSingleton<IEntityLifecycle, EntityLifecycleService>();
        return services;
    }
}
