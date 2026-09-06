using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using MMLib.Alvo.Migrations;
using System.Net.Mime;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Alvo's probe endpoints, owned by the core package so an embedded host gets the same two routes a container
/// does. Deliberately separate from the DI seam (<c>docs/architecture/extensibility.md</c> rule 10).
/// </summary>
public static class AlvoHealthEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <see cref="AlvoHealth.LivenessPath"/> and <see cref="AlvoHealth.ReadinessPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Call it whenever you like — the boot does not have to have run yet.</b> Readiness reads
    /// <see cref="AlvoBootState"/> on every request, and that state reports
    /// <see cref="AlvoBootPhase.Pending"/> until a boot publishes something, so a host that mapped health and
    /// then refused to boot answers 503 rather than throwing into the probe. Liveness answers 200 throughout,
    /// which is the point of splitting them: the process is up, and only its readiness is in question.
    /// </para>
    /// <para>
    /// <b>The two are configured oppositely, on purpose.</b> Liveness evaluates <em>zero</em> checks, so no
    /// health check anyone adds later can start killing containers under load. Readiness evaluates every check
    /// tagged <see cref="AlvoHealth.ReadyTag"/>, so a check registered without much thought lands where being
    /// wrong costs traffic rather than the process. Alvo contributes two: the schema-applied check and the
    /// store-reachability one. Both report <c>Unhealthy</c> and never <c>Degraded</c> — the framework maps
    /// <c>Degraded</c> to <b>200</b> and Kubernetes counts any 2xx as success, so a degraded gate is no gate at
    /// all.
    /// </para>
    /// <para>
    /// <b>Neither route carries a credential, and readiness therefore publishes the phase and nothing else.</b>
    /// A container probe presents nothing to authenticate with, so both are anonymous by construction; and
    /// <see cref="AlvoBootState.Failure"/> — the reason a refused boot recorded — is the database provider's own
    /// message for a stage-1 or stage-2 failure and can carry a connection string. The operator reads that on
    /// stderr and in the log; the probe reads <c>Pending</c>, <c>Ready</c> or <c>Failed</c> (design
    /// deviation 59).
    /// </para>
    /// <para>
    /// Neither response is cacheable: <see cref="HealthCheckOptions.AllowCachingResponses"/> defaults to
    /// <see langword="false"/>, which is what makes the framework send <c>Cache-Control: no-store, no-cache</c>
    /// on both — so there is nothing to configure here, and nothing to regress silently either.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Alvo is not registered in the application's services.</exception>
    public static IEndpointRouteBuilder MapAlvoHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        EnsureAlvoIsRegistered(endpoints);

        endpoints.MapHealthChecks(AlvoHealth.LivenessPath, NoChecksAtAll());
        endpoints.MapHealthChecks(AlvoHealth.ReadinessPath, EveryCheckTaggedReady());

        return endpoints;
    }

    /// <summary>
    /// Refuses at mapping time rather than answering a probe with a 500, which is the one failure a readiness
    /// endpoint must not have: an orchestrator cannot tell it from the schema not being applied yet.
    /// </summary>
    /// <param name="endpoints">The builder being mapped onto.</param>
    private static void EnsureAlvoIsRegistered(IEndpointRouteBuilder endpoints)
    {
        if (endpoints.ServiceProvider.GetService<AlvoBootState>() is null)
        {
            throw new InvalidOperationException(
                "Alvo's health endpoints need Alvo's boot state. Call services.AddAlvo(...) before "
                + "MapAlvoHealth().");
        }
    }

    private static HealthCheckOptions NoChecksAtAll() => new() { Predicate = _ => false };

    /// <summary>
    /// The readiness endpoint's configuration: the tag filter, and a writer that publishes the phase.
    /// </summary>
    /// <remarks>
    /// The <c>HealthReport</c> the framework hands the writer is <b>discarded</b>, and that is the disclosure
    /// guard rather than an economy: an entry's description or exception message belongs to whichever check
    /// produced it, and this body is anonymous. The report's verdict is already carried, faithfully, by the
    /// status code.
    /// </remarks>
    private static HealthCheckOptions EveryCheckTaggedReady() => new()
    {
        Predicate = check => check.Tags.Contains(AlvoHealth.ReadyTag),
        ResponseWriter = (context, _) => WriteTheBootPhase(context),
    };

    private static Task WriteTheBootPhase(HttpContext context)
    {
        var phase = context.RequestServices.GetRequiredService<AlvoBootState>().Phase;

        context.Response.ContentType = MediaTypeNames.Text.Plain;

        return context.Response.WriteAsync(phase.ToString(), context.RequestAborted);
    }
}
