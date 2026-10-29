using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Conventions Alvo adds to endpoint convention builders.</summary>
public static class AlvoEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Applies the conventions <paramref name="configure"/> adds only to the generated routes of <paramref name="entity"/>.
    /// </summary>
    /// <remarks>
    /// Use it on the builder <c>MapAlvoDataApi()</c> returns:
    /// <c>app.MapAlvoDataApi().ForEntity("bookings", b =&gt; b.RequireRateLimiting("bookings"))</c>. A route belongs to an
    /// entity by its <see cref="AlvoEndpointMetadata"/>.
    /// <para>
    /// On the Data API's builder an entity that the descriptor applied when the routes are first built does not declare
    /// fails the route-table build — readiness reports <c>Failed</c> — so a misspelt name is never a rate limit the host
    /// believes it has. The table is built once, on the first routed request, so naming an entity the dashboard applies
    /// only later fails the Data API until the process restarts.
    /// </para>
    /// <para>
    /// On any other builder — a <c>MapGroup</c> the Data API is mapped under — the name is not checked, and every
    /// convention, ordinary or finally, is forwarded through that builder's <c>Finally</c>: a group's ordinary conventions
    /// run before a route's own metadata exists, so they could not tell which entity a route serves. Such conventions
    /// therefore run after the route's own ones, finally conventions included. A builder whose <c>Finally</c> is not
    /// supported throws here rather than reaching no route.
    /// </para>
    /// <para>
    /// A named rate-limiting policy is one partition across every endpoint that names it; to limit each entity on its own,
    /// partition on <see cref="AlvoEndpointMetadata.Entity"/> instead.
    /// </para>
    /// </remarks>
    /// <typeparam name="TBuilder">The convention builder.</typeparam>
    /// <param name="builder">The convention builder, usually the one <c>MapAlvoDataApi()</c> returned.</param>
    /// <param name="entity">The entity whose routes the conventions apply to.</param>
    /// <param name="configure">Adds the conventions, as on any convention builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder ForEntity<TBuilder>(this TBuilder builder, string entity, Action<IEndpointConventionBuilder> configure)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(configure);

        var scoped = new EntityConventions(entity);
        configure(scoped);
        if (builder is AlvoDataApiConventions dataApi)
        {
            AttachToDataApi(dataApi, entity, scoped);
        }
        else
        {
            builder.Finally(scoped.ApplyAll);
        }

        return builder;
    }

    /// <summary>
    /// Attaches the scoped conventions to <c>MapAlvoDataApi()</c>'s own builder, whose conventions run after Alvo's
    /// metadata, and records the name so the route-table build can refuse one that serves nothing.
    /// </summary>
    private static void AttachToDataApi(AlvoDataApiConventions dataApi, string entity, EntityConventions scoped)
    {
        dataApi.Expect(entity);
        dataApi.Add(scoped.Apply);
        if (scoped.HasFinally)
        {
            dataApi.Finally(scoped.ApplyFinally);
        }
    }
}
