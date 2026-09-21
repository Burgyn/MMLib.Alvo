using Microsoft.AspNetCore.Routing;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Alvo's umbrella endpoint seam: one call that maps everything <c>AddAlvo</c> registered and made
/// reachable.
/// </summary>
/// <remarks>
/// Separate from the DI seam by design (<c>docs/architecture/extensibility.md</c> rule 10): adding endpoints
/// never changes how Alvo is registered, and registering Alvo never exposes an endpoint.
/// </remarks>
public static class AlvoEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps Alvo's probe endpoints, the generated Data API and the Management API — the whole HTTP surface a
    /// host gets from the framework, in one call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a composition, not a replacement.</b> <c>MapAlvoHealth()</c>, <c>MapAlvoDataApi()</c> and
    /// <c>MapAlvoManagementApi()</c> stay public for a host that wants the pieces — mounted under different
    /// route groups, say, or with only some of them — exactly as <c>MapControllers</c> coexists with the
    /// finer-grained controller mappings. This method is defined as those three calls and nothing else, and a
    /// test asserts the mappings produce the same endpoint data sources, so the umbrella cannot drift from
    /// its parts.
    /// </para>
    /// <para>
    /// <b>The management surface is mounted here, and it is closed.</b> Every management route carries the
    /// gate the descriptor's <c>access</c> block compiles, and a caller that block does not name is refused —
    /// so a host that calls this method and honours no <c>access</c> block has an administration surface that
    /// admits nobody but the deployment's <b>bootstrap administrator</b>, which is infrastructure
    /// configuration and deliberately above the descriptor (<c>docs/PLAN.md</c> invariant 4), so a project
    /// whose block locks everyone out still has exactly one identity that can fix it. That is the correct
    /// resting state, and it is why mounting it by default is safe: reaching it takes an explicit grant in
    /// the descriptor, or that one configured identity.
    /// </para>
    /// <para>
    /// <b>Health maps first, and the order is load-bearing.</b> <c>MapAlvoDataApi()</c> refuses a host whose
    /// Data API services are absent, and an operator facing that refusal needs a container that can still be
    /// probed: mapping health second would leave one that answers nothing at all, which an orchestrator
    /// cannot tell from a process that is merely slow to start.
    /// </para>
    /// <para>
    /// <b>It returns the route builder, so the management surface's convention builder is discarded here.</b>
    /// A host that wants a convention over the management routes alone — <c>RequireRateLimiting</c>, most
    /// obviously, which this package never applies on a host's behalf — calls
    /// <c>MapAlvoManagementApi()</c> itself and keeps what it returns. Widening this method's return type to
    /// carry one builder out of three would privilege one part of the composition over the others, and
    /// <c>MapAlvo</c>'s value is that it chains like every other <c>Map*</c> a host writes.
    /// </para>
    /// <para>
    /// <b>Calling it stays mandatory, deliberately.</b> Nothing Alvo registers is reachable over HTTP until a
    /// host maps it — the routing documentation's guidance for library authors forbids a library from calling
    /// <c>UseRouting</c>/<c>UseEndpoints</c> on a host's behalf, and nothing may self-register an endpoint
    /// data source outside an explicit <c>Map*</c> call. What this call does <em>not</em> require is an order:
    /// it may run before or after the schema exists, because Alvo's boot primes it before the server binds
    /// and the Data API's routes materialise from that on first enumeration.
    /// </para>
    /// <para>
    /// <b>It does not register <c>AddAlvoProblemDetails()</c>'s error handling</b>, which stays opt-in: an
    /// embedded host has its own, and taking over the shape of <c>UseExceptionHandler</c>'s document inside
    /// someone else's application is worse than one explicit call (design deviation 36).
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Alvo is not registered in the application's services.</exception>
    public static IEndpointRouteBuilder MapAlvo(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapAlvoHealth();
        endpoints.MapAlvoDataApi();
        endpoints.MapAlvoManagementApi();

        return endpoints;
    }
}
