using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// What a host may attach to Alvo's generated routes. <c>MapAlvoDataApi</c> returns the
/// <see cref="IEndpointConventionBuilder"/> every ASP.NET Core <c>Map*</c> returns, so rate limiting, an
/// authorization policy, output caching and telemetry tags land on Alvo's routes and nowhere else.
/// </summary>
/// <remarks>
/// <b>The capability was reachable before this, and that is worth stating rather than overclaiming.</b>
/// <c>app.MapGroup("").MapAlvoDataApi()</c> plus conventions on the group already worked, because
/// <c>AlvoEndpointDataSource.GetGroupedEndpoints</c> forwards the group's context to the nested minimal-API
/// sources. What was missing was the discoverable seam: a host had to know that an empty <c>MapGroup</c> is
/// legal and that Alvo forwards grouped endpoints. The facts below are written against the seam, not against
/// the workaround.
/// </remarks>
public class DataApiConventionTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    private const string OnePerWindow = "one-per-window";

    /// <summary>
    /// The acceptance: a host rate-limits Alvo's routes and the limit is <b>enforced</b> — the second request
    /// is refused by the framework's own middleware reading metadata a convention put there.
    /// </summary>
    [Fact]
    public async Task A_host_can_rate_limit_the_generated_routes_and_the_limit_is_enforced()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureServices: AddOnePerWindowLimiter,
            ConfigureApp: app => app.UseRateLimiter(),
            ConfigureDataApiRoutes: routes => routes.RequireRateLimiting(OnePerWindow)));

        using var first = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);
        using var second = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.ReadTextAsync());
        second.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "the host's rate-limiting convention must reach the generated endpoint");
    }

    /// <summary>
    /// A convention reaches <b>every</b> generated endpoint, not the first one mapped — counted against the
    /// route table rather than against the walk being measured.
    /// </summary>
    [Fact]
    public async Task A_convention_reaches_every_generated_endpoint()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes => routes.WithMetadata(new HostMarker())));

        var generated = Generated(world);

        generated.Count.ShouldBeGreaterThan(0, "a fact about every endpoint needs there to be some");
        generated.Count(endpoint => endpoint.Metadata.GetMetadata<HostMarker>() is not null)
            .ShouldBe(generated.Count);
    }

    /// <summary>
    /// A host's convention is applied <b>after</b> Alvo's own, so it can observe what Alvo attached — and
    /// Alvo's own metadata is still there, which is the half a convention list that replaced rather than
    /// appended would lose.
    /// </summary>
    [Fact]
    public async Task A_hosts_convention_observes_alvos_own_metadata_and_does_not_replace_it()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes => routes.Add(endpoint => endpoint.Metadata.Add(
                new OrderMarker(endpoint.Metadata.OfType<DataApiOperationMetadata>().Any())))));

        var markers = Generated(world)
            .Select(endpoint => endpoint.Metadata.GetMetadata<OrderMarker>())
            .ToList();

        markers.ShouldAllBe(marker => marker != null && marker.SawAlvosMarker);
    }

    /// <summary>
    /// A <c>Finally</c> convention runs too, and runs after the ordinary ones — the half of
    /// <see cref="IEndpointConventionBuilder"/> a hand-rolled implementation silently drops, because its
    /// default interface implementation throws.
    /// </summary>
    [Fact]
    public async Task A_finally_convention_runs_after_the_ordinary_ones()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes =>
            {
                routes.Finally(endpoint => endpoint.Metadata.Add(
                    new FinallyMarker(endpoint.Metadata.OfType<HostMarker>().Any())));
                routes.WithMetadata(new HostMarker());
            }));

        var markers = Generated(world)
            .Select(endpoint => endpoint.Metadata.GetMetadata<FinallyMarker>())
            .ToList();

        markers.ShouldAllBe(marker => marker != null && marker.SawTheOrdinaryOne);
    }

    /// <summary>
    /// A convention added after the route table has materialised <b>throws</b>. It cannot be honoured — the
    /// table is frozen by design — and silently dropping a <c>RequireRateLimiting</c> is a rate limiter a host
    /// believes it has.
    /// </summary>
    [Fact]
    public async Task A_convention_added_after_the_first_request_is_refused()
    {
        IEndpointConventionBuilder? routes = null;
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: builder => routes = builder));

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the route table has to have materialised first");

        var refusal = Should.Throw<InvalidOperationException>(
            () => routes!.WithMetadata(new HostMarker()));

        refusal.Message.ShouldContain("MapAlvoDataApi");
    }

    /// <summary>
    /// A convention that <b>throws</b> leaves no routes and blames the <em>host</em>, not the applied schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Conventions run when the endpoint is built, which is inside the data source's materialisation — where an
    /// <see cref="InvalidOperationException"/> already means "this applied schema cannot be routed" and is
    /// logged at <c>Critical</c> with the descriptor blamed. So the failure this fact drives is deliberately an
    /// <see cref="InvalidOperationException"/>: it is the one a host's own broken <c>RequireRateLimiting</c>
    /// would raise, and the one that used to send an operator to look at their descriptor.
    /// </para>
    /// <para>
    /// The consequence is unchanged and has to be: an exception escaping an <c>EndpointDataSource</c>
    /// enumeration takes down the composite every probe is matched through, liveness included. What moves is
    /// the diagnosis.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_convention_that_throws_leaves_no_routes_and_blames_the_host_not_the_schema()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes => routes.Add(
                _ => throw new InvalidOperationException(HostsOwnBug))));

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound, "a route table that could not be built routes nothing");

        var boot = world.Services.GetRequiredService<AlvoBootState>();
        boot.Phase.ShouldBe(AlvoBootPhase.Failed, "an orchestrator has to be able to drain this pod");
        var reason = boot.Failure.ShouldNotBeNull("a failed boot must publish why");
        reason.ShouldContain("MapAlvoDataApi");
        reason.ShouldContain(HostsOwnBug);
        reason.ShouldNotContain("applied schema", Case.Insensitive, "the descriptor is not what broke here");
    }

    private const string HostsOwnBug = "the policy this host named was never registered";

    /// <summary>
    /// A convention added after a schema the data source <b>refused</b> is refused too — the materialisation
    /// attempt seals whatever its outcome.
    /// </summary>
    /// <remarks>
    /// The sibling fact above covers the happy path, and covering only that was a real gap: a refused schema
    /// installs the empty route table permanently, so <c>Build()</c> never runs again — and sealing from
    /// inside <c>Build()</c> would leave the conventions open forever, silently collecting into a list nothing
    /// will ever read. "Refused, not dropped" has to hold on both outcomes or it is not a contract.
    /// </remarks>
    [Fact]
    public async Task A_convention_added_after_a_refused_schema_is_refused_too()
    {
        IEndpointConventionBuilder? routes = null;
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureServices: services =>
                services.AddSingleton<ISchemaRegistry>(new RegistryShadowingAReservedKey()),
            ConfigureDataApiRoutes: builder => routes = builder));

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound, "the applied schema was refused, so nothing is routed");

        Should.Throw<InvalidOperationException>(() => routes!.WithMetadata(new HostMarker()));
    }

    /// <summary>Every endpoint Alvo generated, identified by the marker it attaches to each of them.</summary>
    private static List<RouteEndpoint> Generated(AlvoApiWorld world) =>
        [.. world.Endpoints.Where(
            endpoint => endpoint.Metadata.GetMetadata<DataApiOperationMetadata>() is not null)];

    private static void AddOnePerWindowLimiter(IServiceCollection services) =>
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddFixedWindowLimiter(OnePerWindow, window =>
            {
                window.PermitLimit = 1;
                window.Window = TimeSpan.FromMinutes(5);
                window.QueueLimit = 0;
                window.AutoReplenishment = false;
            });
        });

    private sealed record HostMarker;

    private sealed record OrderMarker(bool SawAlvosMarker);

    private sealed record FinallyMarker(bool SawTheOrdinaryOne);
}
