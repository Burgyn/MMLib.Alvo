using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Api;

/// <summary>Registers the health checks <c>MapAlvoHealth</c> serves.</summary>
internal static class HealthSetup
{
    /// <summary>
    /// Adds the health-check service and Alvo's own schema-applied check, tagged for readiness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From <c>AddAlvo</c> rather than from the mapping, because a check has to be registered while the
    /// container is still being built and <c>MapAlvoHealth</c> runs after. Registering one exposes nothing —
    /// nothing here is reachable until a host maps the endpoints, which is a separate seam by design
    /// (<c>docs/architecture/extensibility.md</c> rule 10).
    /// </para>
    /// <para>
    /// <b><see cref="HealthCheckServiceCollectionExtensions.AddHealthChecks"/> is idempotent, measured rather
    /// than assumed.</b> It registers exactly two descriptors, both through <c>TryAdd</c>
    /// (<c>HealthCheckService</c> and the publisher's hosted service), so a second and third call add nothing at
    /// all — a host that already called it, as every ASP.NET template does, is unaffected.
    /// </para>
    /// <para>
    /// <b>The check itself is <em>not</em> registered with <c>AddCheck</c>, and that is the whole reason this
    /// type exists.</b> <c>AddCheck</c> is a plain <c>Configure&lt;HealthCheckServiceOptions&gt;</c> and is
    /// therefore additive, so a host that called <c>AddAlvo</c> twice — which every other registration here
    /// supports — would register two checks under one name, and <c>DefaultHealthCheckService</c> refuses to be
    /// constructed at all when two share a name. <c>TryAddEnumerable</c> deduplicates on the implementation
    /// type, which is the same shape <c>AddAlvoApi</c> already uses for its own options setups.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoHealth(this IServiceCollection services)
    {
        services.AddHealthChecks();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IConfigureOptions<HealthCheckServiceOptions>, AlvoSchemaHealthCheckRegistration>());
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IConfigureOptions<HealthCheckServiceOptions>, AlvoReachabilityHealthCheckRegistration>());

        return services;
    }
}

/// <summary>
/// Puts <see cref="AlvoSchemaHealthCheck"/> into the health-check registry under
/// <see cref="AlvoHealth.ReadyTag"/>.
/// </summary>
/// <remarks>
/// <see cref="HealthCheckRegistration.FailureStatus"/> is stated rather than left to its default for the same
/// reason <see cref="AlvoSchemaHealthCheck"/> never reports <see cref="HealthStatus.Degraded"/>: it is what a
/// check that <em>throws</em> is reported as, and 200 for a check that could not run would void the gate just
/// as completely.
/// </remarks>
internal sealed class AlvoSchemaHealthCheckRegistration : IConfigureOptions<HealthCheckServiceOptions>
{
    /// <inheritdoc/>
    public void Configure(HealthCheckServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Registrations.Add(new HealthCheckRegistration(
            AlvoHealth.SchemaCheckName,
            services => new AlvoSchemaHealthCheck(services.GetRequiredService<AlvoBootState>()),
            failureStatus: HealthStatus.Unhealthy,
            tags: [AlvoHealth.ReadyTag]));
    }
}

/// <summary>
/// Puts <see cref="AlvoReachabilityHealthCheck"/> into the health-check registry under
/// <see cref="AlvoHealth.ReadyTag"/>, bounded by <see cref="AlvoHealth.DatabaseProbeTimeout"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetService</c> rather than <c>GetRequiredService</c> for the probe, because not registering one is the
/// supported opt-out — see <see cref="IAlvoDataReachability"/>. Resolved inside the factory rather than here,
/// so a driver whose probe cannot be constructed cannot fail the health-check service's own construction,
/// which would answer <b>500</b> on <em>both</em> probes: the one failure a readiness endpoint must not have.
/// </para>
/// <para>
/// <see cref="HealthCheckRegistration.FailureStatus"/> is stated rather than defaulted for the reason
/// <see cref="AlvoSchemaHealthCheckRegistration"/> gives, and it is also what a probe that <em>timed out</em>
/// is reported as.
/// </para>
/// </remarks>
internal sealed class AlvoReachabilityHealthCheckRegistration : IConfigureOptions<HealthCheckServiceOptions>
{
    /// <inheritdoc/>
    public void Configure(HealthCheckServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Registrations.Add(new HealthCheckRegistration(
            AlvoHealth.DatabaseCheckName,
            CreateCheck,
            failureStatus: HealthStatus.Unhealthy,
            tags: [AlvoHealth.ReadyTag],
            timeout: AlvoHealth.DatabaseProbeTimeout));
    }

    private static AlvoReachabilityHealthCheck CreateCheck(IServiceProvider services) => new(
        services.GetService<IAlvoDataReachability>(),
        services.GetRequiredService<ILogger<AlvoReachabilityHealthCheck>>());
}
