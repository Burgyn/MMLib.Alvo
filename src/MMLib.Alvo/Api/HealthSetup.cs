using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;
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
