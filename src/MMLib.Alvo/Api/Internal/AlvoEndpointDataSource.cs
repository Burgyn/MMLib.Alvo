using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using MMLib.Alvo.Migrations;

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
/// <b>A schema this source refuses to route costs Alvo its readiness, not the host its matcher.</b> The two
/// schema guards below (<see cref="ReservedQueryKeys"/>, <see cref="FormatCatalog"/>) used to throw out of
/// <see cref="Endpoints"/> — and an <see cref="EndpointDataSource"/> is enumerated through the <em>composite</em>
/// of every source the application registered, so one throwing source took down the whole matcher: every request
/// answered 500, <c>/health/live</c> among them, for the life of the process. A failing liveness probe has the
/// container killed and restart-looped, which is the exact outcome <see cref="AlvoHealth.LivenessPath"/> promises
/// nothing anyone registers can cause. So a refusal now records itself on <see cref="AlvoBootState"/> (readiness
/// turns <see cref="AlvoBootPhase.Failed"/>, so an orchestrator drains the pod) and installs an <b>empty</b>
/// endpoint table. Nothing is weakened in the fail-closed direction — a refused schema still has no route at
/// all, and the refusal is logged at <see cref="LogLevel.Critical"/> naming the field — and the refusal for a
/// <em>descriptor</em> is still a start-time one, raised by boot stage 0 before anything is durable.
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="Endpoints"/> is read concurrently — by the matcher and by OpenAPI document
/// generation — so the whole materialised table is published as one immutable snapshot: it is fully constructed
/// before the reference is installed under <see cref="_gate"/> and read back with <c>Volatile.Read</c>,
/// whose acquire semantics stop a reader from using a stale reference or hoisting reads of the snapshot's
/// contents above the load of the reference itself. One reference publication rather than two fields, because
/// the endpoint list and the data sources it was flattened from must never be observed apart. The lock is taken
/// only on the first enumeration; every later read is one volatile load, so no request ever queues behind
/// another's materialisation. A refused build installs the empty table rather than nothing, so the schema is
/// judged exactly once and no second enumeration can reach a different answer.
/// </para>
/// <para>
/// <b>A host's conventions are collected here and applied at materialisation.</b> <c>MapAlvoDataApi</c>
/// returns <see cref="Conventions"/>, and <see cref="Build"/> seals it before mapping, so a convention
/// arriving after the table is frozen is refused rather than dropped — <em>including</em> when the schema
/// guards refused and the frozen table is the empty one, which is the case the sealing's position outside
/// <see cref="BuildOrRefuseToRoute"/>'s <c>try</c> exists for. They are applied inside
/// <c>DataApiEndpoints.Protect</c> — the same call that attaches the authorization filter and the operation
/// marker — so no generated route can be mapped without them. A convention that <em>throws</em> is a distinct
/// diagnosis from a schema that cannot be routed (<see cref="AlvoDataApiConventionException"/>): both end in
/// an empty table and a failed readiness, because an exception escaping this enumeration would take down the
/// composite every probe is matched through, but only one of them is the descriptor's fault.
/// </para>
/// <para>
/// <b>Constructed per <c>MapAlvoDataApi</c> call, never as a process singleton</b>, so serving several projects
/// from one host (#141, parked) needs a second data source rather than a different design here.
/// </para>
/// </remarks>
internal sealed partial class AlvoEndpointDataSource : EndpointDataSource
{
    private readonly EntityRouteCatalog _catalog;
    private readonly AlvoApiOptions _options;
    private readonly AlvoContextFilterFactory _filters;
    private readonly IServiceProvider _services;
    private readonly AlvoBootState _boot;
    private readonly ILogger<AlvoEndpointDataSource> _logger;
    private readonly string _prefix;
    private readonly AlvoDataApiConventions _conventions = new();
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
    /// <param name="boot">Where a schema that cannot be routed is recorded, so readiness reports it.</param>
    /// <param name="logger">Where that refusal is written for the operator who has to read it.</param>
    internal AlvoEndpointDataSource(
        EntityRouteCatalog catalog,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        IServiceProvider services,
        AlvoBootState boot,
        ILogger<AlvoEndpointDataSource> logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(boot);
        ArgumentNullException.ThrowIfNull(logger);

        _catalog = catalog;
        _options = options;
        _filters = filters;
        _services = services;
        _boot = boot;
        _logger = logger;
        _prefix = RoutePrefix.Normalize(options.RoutePrefix);
    }

    /// <summary>
    /// The conventions seam <c>MapAlvoDataApi</c> hands back, so a host can decorate the routes this source
    /// materialises.
    /// </summary>
    internal AlvoDataApiConventions Conventions => _conventions;

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
            var routes = _routes ?? BuildOrRefuseToRoute();
            Volatile.Write(ref _routes, routes);
            return routes;
        }
    }

    /// <summary>
    /// The endpoint table, or an empty one plus a recorded refusal when the schema cannot be routed at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <see cref="InvalidOperationException"/> is treated as a refusal, because that is what both guards
    /// raise and nothing else here is expected to: a reserved field name
    /// (<see cref="ReservedQueryKeys.EnsureNoneIsShadowed(System.Collections.Generic.IEnumerable{Schema.EntitySchema})"/>)
    /// and a <c>format</c> that is not a regular expression (<see cref="FormatCatalog.Build"/>). Anything else —
    /// a defect in route generation — still propagates, because turning an unknown failure into "no routes"
    /// would hide it behind a 404.
    /// </para>
    /// <para>
    /// The refusal is recorded on <see cref="AlvoBootState"/> rather than thrown for the reason the type's remarks
    /// give: throwing out of an <see cref="EndpointDataSource"/> breaks the composite every probe is matched
    /// through, and readiness is the signal that means "this pod cannot serve" without also meaning "kill this
    /// container".
    /// </para>
    /// </remarks>
    private RouteTable BuildOrRefuseToRoute()
    {
        // Outside the try, so the one materialisation attempt seals whatever its outcome: a schema the
        // guards refuse installs the empty table permanently, and conventions left unsealed there would go
        // on being silently collected into a list nothing will ever read.
        _conventions.Seal();

        try
        {
            return Build();
        }
        catch (AlvoDataApiConventionException failure)
        {
            _boot.Failed(failure.Message);
            AHostConventionFailed(_logger, failure.InnerException, failure.Message);

            return RouteTable.NothingIsRoutable;
        }
        catch (InvalidOperationException refusal)
        {
            _boot.Failed(refusal.Message);
            TheSchemaCannotBeRouted(_logger, refusal.Message);

            return RouteTable.NothingIsRoutable;
        }
    }

    /// <summary>The record of an applied schema the Data API refused to build routes for.</summary>
    /// <remarks>
    /// Critical, because the consequence is permanent for this process: the table is frozen empty, so every Data
    /// API request 404s until the schema is fixed and the process restarted. Reachable only for a schema that
    /// never passed descriptor validation — a substituted <c>ISchemaRegistry</c>, a schema applied by an older
    /// build, F7's dynamic entities.
    /// </remarks>
    /// <param name="logger">The logger this source writes through.</param>
    /// <param name="refusal">The refusal, naming the entity and the field.</param>
    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Alvo cannot route the applied schema, so the Data API has no endpoints and readiness reports "
            + "Failed. {Refusal}")]
    private static partial void TheSchemaCannotBeRouted(ILogger logger, string refusal);

    /// <summary>
    /// The record of a convention the <em>host</em> attached to <c>MapAlvoDataApi()</c> throwing while the
    /// endpoints were being built.
    /// </summary>
    /// <remarks>
    /// Critical for the same reason as the schema refusal — the table is frozen empty for the life of the
    /// process — but a separate record, because the fix is in the host's own code and a message blaming the
    /// applied schema would send an operator to the descriptor. The exception is logged with its stack trace,
    /// which is the only place it survives: the endpoint table cannot carry it and the readiness body publishes
    /// the phase alone.
    /// </remarks>
    /// <param name="logger">The logger this source writes through.</param>
    /// <param name="failure">What the host's convention threw.</param>
    /// <param name="reason">The failure, as the message the boot state records.</param>
    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "A convention the host attached to MapAlvoDataApi() failed, so the Data API has no endpoints "
            + "and readiness reports Failed. {Reason}")]
    private static partial void AHostConventionFailed(ILogger logger, Exception? failure, string reason);

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
            DataApiEndpoints.Map(inner, entity, _prefix, _options, _filters, formats, _conventions);
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
        /// <summary>What a refused schema materialises to: no sources, no endpoints, nothing reachable.</summary>
        internal static RouteTable NothingIsRoutable { get; } = new([], []);

        /// <summary>Snapshots what one <see cref="NestedRouteBuilder"/> was mapped onto.</summary>
        /// <param name="inner">The builder the <c>Map*</c> calls wrote into.</param>
        internal static RouteTable Of(NestedRouteBuilder inner)
        {
            IReadOnlyList<EndpointDataSource> sources = [.. inner.DataSources];
            return new RouteTable(sources, [.. sources.SelectMany(source => source.Endpoints)]);
        }
    }
}
