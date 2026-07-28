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
        var prefix = NormalizePrefix(options.RoutePrefix);

        ReservedQueryKeys.EnsureNoneIsShadowed(catalog.Entities);

        foreach (var entity in catalog.Entities)
        {
            DataApiEndpoints.Map(endpoints, entity, prefix, options, filters);
        }

        return endpoints;
    }

    /// <summary>
    /// Reduces a configured prefix to the one form a route pattern can be built from: a single leading
    /// slash and no trailing one, so <c>"api"</c>, <c>"/api"</c> and <c>"/api/"</c> mount in the same place
    /// instead of producing three different route tables.
    /// </summary>
    /// <remarks>
    /// A prefix that is <em>only</em> slashes or whitespace reduces to the empty string, which mounts the
    /// entities at the root (<c>/owners</c>). That case is the one this used to get wrong: it returned
    /// <c>"/"</c>, the caller appended <c>"/owners"</c>, and <c>RoutePatternFactory.Parse("//owners")</c>
    /// threw on the empty segment while the options validator had already reported the value as valid. A
    /// validator returning success is not evidence that a value mounts.
    /// </remarks>
    private static string NormalizePrefix(string prefix)
    {
        var trimmed = prefix?.Trim().Trim('/') ?? string.Empty;
        return trimmed.Length == 0 ? string.Empty : $"/{trimmed}";
    }
}
