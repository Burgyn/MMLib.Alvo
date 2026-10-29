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
    /// entity by its <see cref="AlvoEndpointMetadata"/>. On the Data API's builder an entity the applied descriptor does not
    /// declare fails the route-table build — readiness reports <c>Failed</c> — so a misspelt name is never a rate limit the
    /// host believes it has. On any other builder — a <c>MapGroup</c> the Data API is mapped under — the name is not checked.
    /// A named rate-limiting policy is one partition across every endpoint that names it; to limit each entity on its own,
    /// partition on <see cref="AlvoEndpointMetadata.Entity"/> instead.
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
        (builder as AlvoDataApiConventions)?.Expect(entity);
        builder.Add(scoped.Apply);
        if (scoped.HasFinally)
        {
            builder.Finally(scoped.ApplyFinally);
        }

        return builder;
    }
}
