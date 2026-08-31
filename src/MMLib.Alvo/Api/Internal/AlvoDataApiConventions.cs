using Microsoft.AspNetCore.Builder;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The conventions a host attached to <c>MapAlvoDataApi()</c>, collected until the route table materialises
/// and then applied to every route Alvo maps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Collected rather than applied immediately, because the routes do not exist yet.</b>
/// <see cref="AlvoEndpointDataSource"/> reads the applied schema on the first <em>enumeration</em>, so at the
/// moment a host writes <c>.RequireRateLimiting(…)</c> there is no endpoint to decorate. Collecting adds no
/// ordering obligation to a call whose whole design was to have none: a host's conventions are always complete
/// by the time the first request builds the matcher.
/// </para>
/// <para>
/// <b>After that they are refused, not dropped.</b> The table is frozen once materialised — for the reason
/// <see cref="AlvoEndpointDataSource"/> records — so a late convention cannot be honoured, and a
/// <c>RequireRateLimiting</c> that silently did nothing would be a rate limiter a host believes it has, so the
/// message names the call to move.
/// </para>
/// <para>
/// <b>That is a deliberate deviation from the framework, not conformance to it.</b>
/// <c>RouteEndpointDataSource</c> and <c>RouteHandlerBuilder</c> silently <em>ignore</em> a convention added
/// after the endpoint is built. Alvo throws because its table is frozen once materialised and cannot be
/// amended at all, which makes silence a strictly worse answer here than it is there. The cost is real and
/// stated: a host applying conventions from an <c>IStartupFilter</c> or a hosted service now gets an exception
/// where every other <c>Map*</c> is quiet.
/// </para>
/// <para>
/// <b><see cref="Finally"/> is implemented rather than inherited.</b> Its default interface implementation
/// throws, and forwarding it to the route's own <see cref="IEndpointConventionBuilder.Finally"/> is what keeps
/// the framework's ordering guarantee — a finally-convention observes every ordinary one, Alvo's own metadata
/// included.
/// </para>
/// <para>
/// <b>Guarded by a lock, because the two sides run on different threads.</b> A host adds conventions on the
/// startup thread and the first request materialises the table on a request thread, and <see cref="Seal"/> is
/// what publishes the transition between them.
/// </para>
/// </remarks>
internal sealed class AlvoDataApiConventions : IEndpointConventionBuilder
{
    private readonly List<Action<EndpointBuilder>> _conventions = [];
    private readonly List<Action<EndpointBuilder>> _finallyConventions = [];
    private readonly Lock _gate = new();
    private bool _sealed;

    /// <inheritdoc/>
    public void Add(Action<EndpointBuilder> convention) => Collect(_conventions, convention);

    /// <inheritdoc/>
    public void Finally(Action<EndpointBuilder> finallyConvention) =>
        Collect(_finallyConventions, finallyConvention);

    /// <summary>Applies everything collected to one route Alvo has just mapped.</summary>
    /// <remarks>
    /// Called from inside the data source's materialisation, after Alvo's own filters and metadata, so a
    /// host's convention observes them and can override what it means to.
    /// </remarks>
    /// <param name="route">The route just mapped.</param>
    internal void ApplyTo(IEndpointConventionBuilder route)
    {
        foreach (var convention in _conventions)
        {
            route.Add(endpoint => Invoke(convention, endpoint));
        }

        foreach (var convention in _finallyConventions)
        {
            route.Finally(endpoint => Invoke(convention, endpoint));
        }
    }

    /// <summary>Closes the collection, so a later addition is refused instead of ignored.</summary>
    internal void Seal()
    {
        lock (_gate)
        {
            _sealed = true;
        }
    }

    /// <summary>
    /// Runs one host convention, labelling anything it throws as the <em>host's</em> failure rather than
    /// Alvo's.
    /// </summary>
    /// <remarks>
    /// <b>The label is the whole point.</b> A convention runs when the endpoint is built, which is inside
    /// <see cref="AlvoEndpointDataSource"/>'s materialisation — where an <see cref="InvalidOperationException"/>
    /// already means "this applied schema cannot be routed" and is logged at <c>Critical</c> with the
    /// descriptor blamed. A host whose own <c>RequireRateLimiting</c> refers to a policy it never registered
    /// would have read that message and gone looking at their descriptor. Wrapping keeps the two diagnoses
    /// apart while leaving the consequence identical, because it has to be: an exception escaping an
    /// <c>EndpointDataSource</c> enumeration takes down the composite every probe is matched through.
    /// </remarks>
    /// <param name="convention">The host's convention.</param>
    /// <param name="endpoint">The endpoint being built.</param>
    /// <exception cref="AlvoDataApiConventionException"><paramref name="convention"/> threw.</exception>
    private static void Invoke(Action<EndpointBuilder> convention, EndpointBuilder endpoint)
    {
        try
        {
            convention(endpoint);
        }
        catch (Exception failure)
        {
            throw new AlvoDataApiConventionException(failure);
        }
    }

    private void Collect(List<Action<EndpointBuilder>> conventions, Action<EndpointBuilder> convention)
    {
        ArgumentNullException.ThrowIfNull(convention);

        lock (_gate)
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "Alvo's Data API routes have already been built, so this convention cannot be applied. "
                    + "Attach conventions to the builder MapAlvoDataApi() returns before the first request "
                    + "reaches the application.");
            }

            conventions.Add(convention);
        }
    }
}

/// <summary>
/// A convention the host attached to <c>MapAlvoDataApi()</c> threw while its endpoint was being built.
/// </summary>
/// <remarks>
/// Its only job is to be a <em>distinct type</em>, so <see cref="AlvoEndpointDataSource"/> can tell a host's
/// broken convention from an applied schema it cannot route. Both end the same way — an empty endpoint table
/// and readiness reporting failed — but only one of them is the descriptor's fault, and an operator reading
/// the wrong one looks in the wrong place.
/// </remarks>
/// <param name="failure">What the host's convention threw.</param>
internal sealed class AlvoDataApiConventionException(Exception failure)
    : Exception(BuildMessage(failure), failure)
{
    private static string BuildMessage(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return "A convention attached to MapAlvoDataApi() threw while Alvo was building its endpoints, so the "
            + $"Data API has no routes: {failure.Message}";
    }
}
