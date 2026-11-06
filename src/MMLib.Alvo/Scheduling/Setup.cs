using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
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
        services.TryAddSingleton(provider => new TimerPump(
            provider.GetService<ITimerStore>(),
            provider.GetRequiredService<AlvoBootState>(),
            provider.GetRequiredService<IPolicyCatalogProvider>(),
            provider.GetRequiredService<IOptions<AlvoEventOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<TimerPump>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TimerPump>(provider => provider.GetRequiredService<TimerPump>()));
        services.TryAddSingleton(provider => new ScheduleRuns(provider.GetService<IAlvoTenantCatalog>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(provider => new DataMigrationBootCheck(
            provider.GetRequiredService<ExtensionRegistry>(),
            provider.GetRequiredService<IServiceProviderIsService>(),
            provider.GetService<ITimerStore>()));
        services.TryAddSingleton(provider => new DataMigrationStarter(
            provider.GetRequiredService<ScheduleRuns>(),
            provider.GetService<IAppliedSchemaStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<DataMigrationStarter>>()));
        services.TryAddSingleton(provider => new SchedulePump(
            provider.GetService<ITimerStore>(),
            provider.GetRequiredService<ExtensionBinder>(),
            provider.GetRequiredService<ScheduleRuns>(),
            provider.GetRequiredService<DataMigrationStarter>(),
            provider.GetRequiredService<AlvoBootState>(),
            provider.GetRequiredService<IPolicyCatalogProvider>(),
            provider.GetRequiredService<IOptions<AlvoEventOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<SchedulePump>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SchedulePump>(provider => provider.GetRequiredService<SchedulePump>()));

        return services;
    }

    private static InvalidOperationException NoTimerStore() => new(
        "IAlvoScheduler needs a timer store, and no data provider registered one. Fix: configure a relational provider "
        + "(UseSqlite or UsePostgreSql), whose store keeps timers beside the outbox.");
}
