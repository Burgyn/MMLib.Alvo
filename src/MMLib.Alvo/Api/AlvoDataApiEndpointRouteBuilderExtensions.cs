using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Migrations;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// The generated Data API's endpoint seam, owned by the core package. Deliberately separate from the DI
/// seam (<c>docs/architecture/extensibility.md</c> rule 10): adding endpoints never changes how Alvo is
/// registered, and registering Alvo never exposes an endpoint.
/// </summary>
public static class AlvoDataApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Registers the Data API's endpoint data source, which maps one minimal-API delegate per operation per
    /// entity in the applied schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Call this whenever you like — the applied schema does not have to exist yet.</b> Route literals are
    /// entity names read from the applied schema, and the read happens when the endpoint table is first
    /// <em>enumerated</em> (the first request that builds the matcher), not here. So a host maps declaratively
    /// and Alvo's boot primes the schema afterwards, before the server binds:
    /// <c>register → map → boot → listen → first request materialises the routes</c>. An entity the applied
    /// schema does not declare still has no route, which is the fail-closed direction. A descriptor applied
    /// <em>later</em> at runtime still takes effect for policy and validation immediately; it cannot add a route
    /// literal to an endpoint table that has already materialised (#103).
    /// </para>
    /// <para>
    /// <b>The data source is registered even when the schema declares nothing</b>, and that is load-bearing:
    /// <c>WebApplicationBuilder</c> decides whether to add <c>UseRouting</c>/<c>UseEndpoints</c> at all from
    /// <c>DataSources.Count &gt; 0</c> — it counts <em>sources</em>, not endpoints — so registering an empty one
    /// is both necessary and sufficient, and registering none would leave routing out of the pipeline where no
    /// later priming could put it back. Alvo never calls <c>UseRouting</c> or <c>UseEndpoints</c> on the host's
    /// behalf, which the routing docs' guidance for library authors forbids outright.
    /// </para>
    /// <para>
    /// <b>Where the two schema guards fire, now that enumeration is lazy.</b> The reserved-query-key check and
    /// the format-catalogue build run inside <see cref="AlvoEndpointDataSource"/>, over the schema
    /// <c>ISchemaRegistry</c> answers with — so they fire on the <em>first enumeration</em>, not at this call.
    /// Neither is the start-time refusal: <c>DescriptorBootPlan</c> (boot stage 0) runs both over the
    /// descriptor's own mapped schema and fails the <em>start</em>, before anything is durable. The pair here is
    /// the defence-in-depth belt for a <see cref="MMLib.Alvo.Schema.ISchemaRegistry"/> that no descriptor
    /// validation ever passed through — a substituted registry, a schema applied by an older build, F7's dynamic
    /// entities — and for that input alone a refusal can only surface on the first request. Recorded as a
    /// deviation in the design rather than presented as a start-time guarantee it is not.
    /// </para>
    /// <para>
    /// <b>And for that input the refusal costs Alvo its readiness, not the host its matcher.</b> A schema neither
    /// guard will route leaves this source with an <em>empty</em> endpoint table and records the reason on
    /// <c>AlvoBootState</c>, so <c>/health/ready</c> reports <c>Failed</c> while <c>/health/live</c> keeps
    /// answering. Throwing out of an <c>EndpointDataSource</c> instead — which is what this did — took down the
    /// composite the framework matches <em>every</em> request through, liveness included, and a failing liveness
    /// probe is how a pod gets killed and restart-looped for a schema no restart can fix.
    /// </para>
    /// <para>
    /// Every mapped endpoint carries the API-key context filter — attached in the same call as the operation
    /// marker and as the host's own conventions — so nothing <em>this framework</em> maps has a path to
    /// <c>IAlvoData</c> that skips the authorization seam. A convention the host attaches receives the
    /// endpoint builder and could take it away again; that is host code deciding to dismantle its own
    /// pipeline, which it could already do by substituting <c>IPolicyEngine</c>, and it is not what this
    /// sentence claims.
    /// </para>
    /// <para>
    /// <b>It returns a convention builder rather than the route builder it was given, which is what every
    /// ASP.NET Core <c>Map*</c> does</b> — <c>MapHealthChecks</c> and <c>MapControllers</c> included. The
    /// capability was reachable before: <c>app.MapGroup("").MapAlvoDataApi()</c> plus conventions on the
    /// group worked, because <see cref="AlvoEndpointDataSource.GetGroupedEndpoints"/> forwards the group's
    /// context to the nested minimal-API sources. What it was not, was discoverable. Conventions have to be
    /// attached before the first request materialises the route table; one attached after is <em>refused</em>,
    /// because a frozen table cannot honour it and a silently dropped <c>RequireRateLimiting</c> is a rate
    /// limiter a host believes it has.
    /// </para>
    /// <para>
    /// <c>MapAlvo()</c> deliberately still returns the route builder, and <c>MapAlvoHealth()</c> is not
    /// chainable at all: one convention builder over the probes <em>and</em> the Data API would let a host
    /// attach an authorization policy to <c>/health/live</c>, and a container probe presents no credential —
    /// that is a container killed and restart-looped by its own liveness gate. A host that wants conventions
    /// calls the parts, which is already the documented composition.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>
    /// A convention builder over the routes this call will materialise, so a host can attach
    /// <c>RequireRateLimiting</c>, an authorization policy, output caching or a telemetry tag to Alvo's
    /// generated endpoints and to nothing else.
    /// </returns>
    /// <exception cref="InvalidOperationException">Alvo is not registered in the application's services.</exception>
    public static IEndpointConventionBuilder MapAlvoDataApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var catalog = services.GetService<EntityRouteCatalog>()
            ?? throw new InvalidOperationException(
                "The Alvo Data API is not registered. Call services.AddAlvo(...) — optionally with "
                + "AddDataApi(...) to configure it — before MapAlvoDataApi().");

        var source = new AlvoEndpointDataSource(
            catalog,
            services.GetRequiredService<IOptions<AlvoApiOptions>>().Value,
            services.GetRequiredService<AlvoContextFilterFactory>(),
            services,
            services.GetRequiredService<AlvoBootState>(),
            services.GetRequiredService<ILogger<AlvoEndpointDataSource>>());

        endpoints.DataSources.Add(source);

        return source.Conventions;
    }
}
