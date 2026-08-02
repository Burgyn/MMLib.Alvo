using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// The generated Data API's endpoint seam, owned by the core package. Deliberately separate from the DI
/// seam (<c>docs/architecture/extensibility.md</c> rule 10): adding endpoints never changes how Alvo is
/// registered, and registering Alvo never exposes an endpoint.
/// </summary>
public static class AlvoDataApiEndpointRouteBuilderExtensions
{
    /// <summary>Maps one minimal-API delegate per operation per entity in the applied schema.</summary>
    /// <remarks>
    /// <para>
    /// <b>Call this after the descriptor has been applied.</b> Routes are entity-name <em>literals</em>
    /// read from the applied schema, so an unapplied descriptor maps nothing at all — which is the safe
    /// direction (no route beats a route with no schema behind it), and is why a host runs its migration
    /// before mapping. A descriptor applied <em>later</em> at runtime still takes effect for policy and
    /// validation immediately; it cannot add a route literal to an endpoint table that is already built.
    /// </para>
    /// <para>
    /// Every mapped endpoint carries the API-key context filter, so this surface has no path to
    /// <c>IAlvoData</c> that skips the authorization seam.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">Alvo is not registered in the application's services.</exception>
    public static IEndpointRouteBuilder MapAlvoDataApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var catalog = services.GetService<EntityRouteCatalog>()
            ?? throw new InvalidOperationException(
                "The Alvo Data API is not registered. Call services.AddAlvo(...) — optionally with "
                + "AddDataApi(...) to configure it — before MapAlvoDataApi().");

        var options = services.GetRequiredService<IOptions<AlvoApiOptions>>().Value;
        var filters = services.GetRequiredService<AlvoContextFilterFactory>();
        var prefix = RoutePrefix.Normalize(options.RoutePrefix);

        ReservedQueryKeys.EnsureNoneIsShadowed(catalog.Entities);

        // One catalogue for the whole applied descriptor, compiled here rather than per request — see
        // FormatCatalog for why a caller-supplied value against an author-supplied pattern is a ReDoS
        // surface, and why the compilation belongs at the same instant the route literals are fixed.
        var formats = FormatCatalog.Build(catalog.Entities);

        foreach (var entity in catalog.Entities)
        {
            DataApiEndpoints.Map(endpoints, entity, prefix, options, filters, formats);
        }

        return endpoints;
    }
}
