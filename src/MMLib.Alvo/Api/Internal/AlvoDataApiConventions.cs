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
    private readonly HashSet<string> _expected = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _sealed;

    /// <summary>The entities a <c>ForEntity</c> on this builder named, which the applied schema must declare.</summary>
    internal IReadOnlyCollection<string> ExpectedEntities
    {
        get
        {
            lock (_gate)
            {
                return [.. _expected];
            }
        }
    }

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

    /// <summary>Records that a <c>ForEntity</c> named <paramref name="entity"/>, so the build can refuse a name that serves nothing.</summary>
    /// <param name="entity">The entity named.</param>
    internal void Expect(string entity)
    {
        lock (_gate)
        {
            EnsureOpen();
            _expected.Add(entity);
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
            EnsureOpen();
            conventions.Add(convention);
        }
    }

    /// <summary>Refuses an addition once the table is built; called under <c>_gate</c>.</summary>
    /// <exception cref="InvalidOperationException">The routes have already been built.</exception>
    private void EnsureOpen()
    {
        if (_sealed)
        {
            throw new InvalidOperationException(
                "Alvo's Data API routes have already been built, so this convention cannot be applied. "
                + "Attach conventions to the builder MapAlvoDataApi() returns before the first request "
                + "reaches the application.");
        }
    }
}

/// <summary>
/// A convention the host attached to <c>MapAlvoDataApi()</c> broke the build: it threw while its endpoint was being
/// built, or a <c>ForEntity</c> named an entity the applied descriptor does not declare.
/// </summary>
/// <remarks>
/// Its only job is to be a <em>distinct type</em>, so <see cref="AlvoEndpointDataSource"/> can tell a host's
/// broken convention from an applied schema it cannot route. Both end the same way — an empty endpoint table
/// and readiness reporting failed — but only one of them is the descriptor's fault, and an operator reading
/// the wrong one looks in the wrong place.
/// </remarks>
#pragma warning disable RCS1194 // Deliberately no standard constructors: each failure is one of two named diagnoses.
internal sealed class AlvoDataApiConventionException : Exception
{
    /// <summary>A convention the host attached threw.</summary>
    /// <param name="failure">What the host's convention threw.</param>
    internal AlvoDataApiConventionException(Exception failure)
        : base(BuildMessage(failure), failure)
    {
    }

    private AlvoDataApiConventionException(string message)
        : base(message)
    {
    }

    /// <summary>A <c>ForEntity</c> named an entity the applied schema does not declare.</summary>
    /// <param name="entity">The name it used.</param>
    /// <param name="declared">The entities the schema declares.</param>
    internal static AlvoDataApiConventionException NoSuchEntity(string entity, IEnumerable<string> declared) => new(
        $"ForEntity('{entity}') on MapAlvoDataApi() names no entity the applied descriptor declares, so its conventions would "
        + $"reach no route and the Data API has no routes. Declared: {string.Join(", ", declared)}. Name one of them.");

    /// <summary>A <c>ForEntity</c> on a group added a filter, which a group's late conventions can never compose.</summary>
    /// <param name="entity">The name it used.</param>
    internal static AlvoDataApiConventionException FilterTooLate(string entity) => new(
        $"ForEntity('{entity}') on a MapGroup added an endpoint filter, so the Data API has no routes. A group reaches a "
        + "route only through Finally, after its filters are composed, so the filter would never run. Add it through "
        + $"ForEntity('{entity}') on the builder MapAlvoDataApi() returns instead.");

    /// <summary>A convention removed Alvo's authorization filter from a generated route (issue 184).</summary>
    /// <param name="witness">The route whose gate was not composed.</param>
    internal static AlvoDataApiConventionException GateRemoved(AlvoGateWitness witness) => new(
        $"A convention removed Alvo's authorization filter from {witness.Route}, so the Data API has no routes. A convention "
        + "may add filters around Alvo's; it may not remove, replace or rebuild them. Look for a convention — on "
        + "MapAlvoDataApi(), in a ForEntity, or on a MapGroup the Data API is mapped under — that clears or rewrites "
        + "EndpointBuilder.FilterFactories.");

    private static string BuildMessage(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return "A convention attached to MapAlvoDataApi() threw while Alvo was building its endpoints, so the "
            + $"Data API has no routes: {failure.Message}";
    }
}
#pragma warning restore RCS1194
