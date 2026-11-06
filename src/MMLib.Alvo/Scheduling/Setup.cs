using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Scheduling;

/// <summary>Registers the time feature; called from <c>AddAlvo</c> and nowhere else.</summary>
internal static class SchedulingSetup
{
    internal static IServiceCollection AddAlvoScheduling(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAlvoScheduler>(provider => new AlvoScheduler(
            provider.GetService<ITimerStore>() ?? throw NoTimerStore(),
            provider.GetRequiredService<IPolicyCatalogProvider>(),
            provider.GetRequiredService<AlvoChainDepthLimit>(),
            provider.GetRequiredService<TimeProvider>()));

        return services;
    }

    private static InvalidOperationException NoTimerStore() => new(
        "IAlvoScheduler needs a timer store, and no data provider registered one. Fix: configure a relational provider "
        + "(UseSqlite or UsePostgreSql), whose store keeps timers beside the outbox.");
}
