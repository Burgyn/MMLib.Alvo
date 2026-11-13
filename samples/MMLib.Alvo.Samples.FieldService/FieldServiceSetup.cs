using Microsoft.Extensions.Options;
using MMLib.Alvo.Samples.FieldService.Dispatch;
using MMLib.Alvo.Samples.FieldService.Mobile;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.Samples.FieldService.Notifications;
using MMLib.Alvo.Samples.FieldService.Reads;
using MMLib.Alvo.Samples.FieldService.Reports;
using MMLib.Alvo.Samples.FieldService.Scheduling;
using MMLib.Alvo.Samples.FieldService.Sla;
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
                .BeforeWrite<WithinServiceHours>(WithinServiceHours.Id)
                .OnUpdated<NotifyCustomerOfVisit>(
                    NotifyCustomerOfVisit.Id,
                    when: e => e.Changed(x => x.Status) && e.New.Status == WorkOrderStatus.Scheduled)
                .Narrow<TechnicianOpenJobs>(TechnicianOpenJobs.Id))
            .AddEventHandler<SlaBreached, EscalateSlaBreach>(EscalateSlaBreach.Id)
            .AddSchedule<OverdueReportJob>(OverdueReportJob.Id, options.OverdueReportCron, options.TimeZone, schedule =>
            {
                schedule.Scope = AlvoScheduleScope.EachTenant;
                schedule.Description = OverdueReportJob.Description;
            });
    }

    /// <summary>Registers this host's own services: its options, its tenant catalog and its notifier.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration the <c>FieldService</c> section is bound from.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddFieldServiceServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FieldServiceOptions>()
            .Bind(configuration.GetSection(FieldServiceOptions.SectionName))
            .Validate(options => IsAbsoluteUrl(options.NotificationsUrl), NotificationsUrlRequired)
            .ValidateOnStart();
        services.AddSingleton<IAlvoTenantCatalog, ConfiguredTenantCatalog>();
        services.AddHttpClient<IFieldServiceNotifier, HttpFieldServiceNotifier>((provider, http) =>
            http.BaseAddress = new Uri(provider.GetRequiredService<IOptions<FieldServiceOptions>>().Value.NotificationsUrl.TrimEnd('/') + "/"));

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

    private const string NotificationsUrlRequired =
        "FieldService:NotificationsUrl must be an absolute http(s) URL: where the customer's visit notice, the on-call page "
        + "and the overdue report are posted (an SMS or e-mail gateway; the WireMock sidecar in the demo stack).";

    private static bool IsAbsoluteUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    /// <summary>The settings as configured, for the composition steps that need them before the container is built.</summary>
    /// <param name="configuration">The configuration.</param>
    internal static FieldServiceOptions ReadOptions(IConfiguration configuration) =>
        configuration.GetSection(FieldServiceOptions.SectionName).Get<FieldServiceOptions>() ?? new FieldServiceOptions();
}
