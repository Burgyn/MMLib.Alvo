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
    /// Maps Alvo's probe endpoints and the generated Data API — the whole HTTP surface a host gets from the
    /// framework, in one call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a composition, not a replacement.</b> <c>MapAlvoHealth()</c> and <c>MapAlvoDataApi()</c> stay
    /// public for a host that wants the pieces — mounted under different route groups, say, or with only one
    /// of them — exactly as <c>MapControllers</c> coexists with the finer-grained controller mappings. This
    /// method is defined as those two calls and nothing else, and a test asserts the two mappings produce the
    /// same endpoint data sources, so the umbrella cannot drift from its parts.
    /// </para>
    /// <para>
    /// <b>Health maps first, and the order is load-bearing.</b> <c>MapAlvoDataApi()</c> refuses a host whose
    /// Data API services are absent, and an operator facing that refusal needs a container that can still be
    /// probed: mapping health second would leave one that answers nothing at all, which an orchestrator
    /// cannot tell from a process that is merely slow to start.
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

        return endpoints;
    }
}
