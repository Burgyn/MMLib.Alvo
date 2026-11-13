using MMLib.Alvo.Samples.FieldService.Dispatch;
using MMLib.Alvo.Samples.FieldService.Mobile;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.Samples.FieldService.Scheduling;
using MMLib.Alvo.Samples.FieldService.Tenancy;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Samples.FieldService;

/// <summary>Every slice's registration, in the three places an embedded host composes Alvo.</summary>
/// <remarks>
/// The split follows the seam, not the slices: <see cref="AddFieldService"/> runs inside <c>AddAlvo</c> beside
/// <c>FromDescriptor</c>, <see cref="AddFieldServiceServices"/> registers this host's own services, and
/// <see cref="MapFieldService"/> maps its routes. A test composes the same three over its own database.
/// </remarks>
public static class FieldServiceSetup
{
    /// <summary>Registers every extension this host adds to the descriptor.</summary>
    /// <param name="alvo">The Alvo builder.</param>
    /// <param name="options">The host's settings, read where the host is composed (the overdue report's cron).</param>
    /// <returns><paramref name="alvo"/>, for chaining.</returns>
    public static IAlvoBuilder AddFieldService(this IAlvoBuilder alvo, FieldServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(alvo);
        ArgumentNullException.ThrowIfNull(options);

        return alvo
            .Entity<WorkOrder>(workOrders => workOrders
                .BeforeWrite<WithinServiceHours>(WithinServiceHours.Id));
    }

    /// <summary>Registers this host's own services: its options, its tenant catalog and its notifier.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration the <c>FieldService</c> section is bound from.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddFieldServiceServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<FieldServiceOptions>(configuration.GetSection(FieldServiceOptions.SectionName));
        services.AddSingleton<IAlvoTenantCatalog, ConfiguredTenantCatalog>();

        return services;
    }

    /// <summary>Maps this host's own routes: the dispatch command and the technician app's webhook.</summary>
    /// <param name="app">The application to map onto.</param>
    /// <returns><paramref name="app"/>, for chaining.</returns>
    public static WebApplication MapFieldService(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.MapDispatch().MapTechnicianApp();
    }

    /// <summary>The settings as configured, for the composition steps that need them before the container is built.</summary>
    /// <param name="configuration">The configuration.</param>
    internal static FieldServiceOptions ReadOptions(IConfiguration configuration) =>
        configuration.GetSection(FieldServiceOptions.SectionName).Get<FieldServiceOptions>() ?? new FieldServiceOptions();
}
