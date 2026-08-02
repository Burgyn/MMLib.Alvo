using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The Data API's endpoints, read from the applied schema at <b>enumeration</b> time rather than at the moment
/// a host called <c>MapAlvoDataApi</c> — which is what lets a host map declaratively before the boot has primed
/// anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>An <see cref="EndpointDataSource"/> is not enumerated during <c>StartAsync</c>; it is enumerated by the
/// first request that builds the matcher</b> (design fact 1, measured). So the sequence a host writes becomes
/// <c>register → map → boot/prime → listen → first request materialises the routes</c>, and the ordering
/// obligation "apply before you map" disappears. An entity the applied schema does not declare still has no
/// route at all, so laziness costs nothing in the fail-closed direction.
/// </para>
/// <para>
/// <b>The endpoints are built through <see cref="DataApiEndpoints.Map"/> on a
/// <see cref="NestedRouteBuilder"/> — the genuine minimal-API <c>Map*</c> helpers — and that is a requirement,
/// not a style choice.</b> Hand-assembled <c>RouteEndpointBuilder</c> endpoints route perfectly and are
/// invisible to ApiExplorer, so the OpenAPI document empties while every routing test stays green (design
/// facts 4 and 5, measured). A future "simplification" to hand-built endpoints is therefore a regression, and
/// <c>LazyRouteMaterialisationTests.The_OpenApi_document_lists_every_mapped_entity_route</c> is what catches
/// it.
/// </para>
/// <para>
/// <b>The schema is read exactly once, and the endpoint list is then frozen.</b> Not merely as an economy: a
/// source that rebuilt on every enumeration would let the OpenAPI document — which is generated per request and
/// enumerates afresh — advertise an entity applied at runtime that the matcher, cached behind an unfired change
/// token, does not route. Freezing keeps the two answers identical, which is the same reason
/// <see cref="EntityRouteCatalog"/> exists as one authority. Adding a route at runtime is #103's work and needs
/// a change token, not a second enumeration.
/// </para>
/// <para>
/// <b><see cref="GetChangeToken"/> never fires.</b> When #103 grows the mutable half, the new token must be
/// published <em>before</em> the old one is cancelled: the reverse order re-enters the invalidation and
/// overflows the stack (aspnetcore#44392).
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="Endpoints"/> is read concurrently — by the matcher and by OpenAPI document
/// generation — so the whole materialised table is published as one immutable snapshot: it is fully constructed
/// before the reference is installed under <see cref="_gate"/> and read back with <c>Volatile.Read</c>,
/// whose acquire semantics stop a reader from using a stale reference or hoisting reads of the snapshot's
/// contents above the load of the reference itself. One reference publication rather than two fields, because
/// the endpoint list and the data sources it was flattened from must never be observed apart. The lock is taken
/// only on the first enumeration; every later read is one volatile load, so no request ever queues behind
/// another's materialisation. A build that throws installs nothing, so the refusal is raised again — identically
/// — on the next enumeration rather than leaving a half-built table behind.
/// </para>
/// <para>
/// <b>Constructed per <c>MapAlvoDataApi</c> call, never as a process singleton</b>, so serving several projects
/// from one host (#141, parked) needs a second data source rather than a different design here.
/// </para>
/// </remarks>
internal sealed class AlvoEndpointDataSource : EndpointDataSource
{
    private readonly EntityRouteCatalog _catalog;
    private readonly AlvoApiOptions _options;
    private readonly AlvoContextFilterFactory _filters;
    private readonly IServiceProvider _services;
    private readonly string _prefix;
    private readonly Lock _gate = new();
    private RouteTable? _routes;

    /// <summary>Initializes a new instance of the <see cref="AlvoEndpointDataSource"/> class.</summary>
    /// <remarks>
    /// Everything except the schema itself is resolved by the caller, at map time: the options and the filter
    /// factory are fixed for the process, so resolving them eagerly keeps a misconfigured
    /// <see cref="AlvoApiOptions"/> a failure of the <c>MapAlvoDataApi</c> call rather than of the first
    /// request. The applied schema is the one input that is not yet knowable then, and it is the only one read
    /// lazily.
    /// </remarks>
    /// <param name="catalog">The applied schema's entities — read once, on the first enumeration.</param>
    /// <param name="options">The API options every mapped endpoint reads its paging and payload bounds from.</param>
    /// <param name="filters">Builds the authorization filter each mapped endpoint carries.</param>
    /// <param name="services">The application's services, which the mapped delegates resolve their arguments from.</param>
    internal AlvoEndpointDataSource(
        EntityRouteCatalog catalog,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(services);

        _catalog = catalog;
        _options = options;
        _filters = filters;
        _services = services;
        _prefix = RoutePrefix.Normalize(options.RoutePrefix);
    }

    /// <inheritdoc/>
    public override IReadOnlyList<Endpoint> Endpoints => Materialise().Endpoints;

    /// <inheritdoc/>
    /// <remarks>
    /// Forwarded to the nested sources the <c>Map*</c> helpers wrote into, rather than left to the base
    /// implementation, for the same reason the endpoints are built through those helpers at all: it is the
    /// framework's own minimal-API data source that knows how to re-run a route handler's conventions and
    /// filters under a group's prefix. <c>app.MapGroup(prefix).MapAlvoDataApi()</c> is a supported call, and a
    /// created row's <c>Location</c> is read off the matched endpoint's combined pattern (#121).
    /// </remarks>
    public override IReadOnlyList<Endpoint> GetGroupedEndpoints(RouteGroupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return [.. Materialise().Sources.SelectMany(source => source.GetGroupedEndpoints(context))];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A token that never fires, because this source's endpoint table never changes once materialised. The
    /// mutable half — invalidating so a runtime-applied entity gains a route — is #103's, and its measured cost
    /// is recorded there: the OpenAPI document has its own cache and does <em>not</em> refresh when this source
    /// invalidates (design fact 6).
    /// </remarks>
    public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;

    /// <summary>The materialised endpoint table, built on the first read and frozen thereafter.</summary>
    private RouteTable Materialise()
    {
        if (Volatile.Read(ref _routes) is { } materialised)
        {
            return materialised;
        }

        lock (_gate)
        {
            var routes = _routes ?? Build();
            Volatile.Write(ref _routes, routes);
            return routes;
        }
    }

    /// <summary>
    /// Maps one entity's five routes for every entity the applied schema declares, and flattens what the
    /// <c>Map*</c> helpers produced.
    /// </summary>
    /// <remarks>
    /// The schema is read into a local first, so the guard below, the endpoints and the format catalogue are
    /// all built from <em>one</em> reading of <see cref="EntityRouteCatalog.Entities"/> — a second reading is
    /// how a table comes to carry a route for an entity the guard never saw.
    /// </remarks>
    private RouteTable Build()
    {
        var entities = _catalog.Entities;
        ReservedQueryKeys.EnsureNoneIsShadowed(entities);

        var formats = FormatCatalog.Build(entities);
        var inner = new NestedRouteBuilder(_services);
        foreach (var entity in entities)
        {
            DataApiEndpoints.Map(inner, entity, _prefix, _options, _filters, formats);
        }

        return RouteTable.Of(inner);
    }

    /// <summary>
    /// One materialisation: the nested sources the <c>Map*</c> helpers wrote into, and the endpoints they
    /// produced.
    /// </summary>
    /// <remarks>
    /// Both halves are published as one reference because both are needed and they must agree:
    /// <see cref="Endpoints"/> serves the flattened list, and <see cref="GetGroupedEndpoints"/> has to ask the
    /// same sources again with a group's prefix.
    /// </remarks>
    /// <param name="Sources">The nested data sources, in the order the entities were mapped.</param>
    /// <param name="Endpoints">Every endpoint those sources produced, flattened.</param>
    private sealed record RouteTable(IReadOnlyList<EndpointDataSource> Sources, IReadOnlyList<Endpoint> Endpoints)
    {
        /// <summary>Snapshots what one <see cref="NestedRouteBuilder"/> was mapped onto.</summary>
        /// <param name="inner">The builder the <c>Map*</c> calls wrote into.</param>
        internal static RouteTable Of(NestedRouteBuilder inner)
        {
            IReadOnlyList<EndpointDataSource> sources = [.. inner.DataSources];
            return new RouteTable(sources, [.. sources.SelectMany(source => source.Endpoints)]);
        }
    }
}
