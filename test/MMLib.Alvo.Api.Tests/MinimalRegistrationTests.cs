using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;
using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The registration surface a host actually writes: <c>AddAlvo(…)</c> and <c>MapAlvo()</c>, and nothing
/// whose order it has to know.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this suite is about is the <em>ordering obligation</em>, not the call count.</b> The old shape was
/// four calls whose sequence was load-bearing folklore — apply before mapping, check the result, and
/// <c>EnsureApplied</c> or serve nothing while reporting healthy. None of that is expressible in a type, so
/// the only place it can be pinned is a fact that writes the host the way the documentation now promises it
/// can be written and then asserts the backend works.
/// </para>
/// <para>
/// <b><c>MapAlvo()</c> stays mandatory, and that is deliberate rather than an omission.</b> The routing
/// documentation's guidance for library authors forbids calling <c>UseRouting</c>/<c>UseEndpoints</c> on a
/// host's behalf, and nothing may register an endpoint data source outside an explicit <c>Map*</c> call.
/// <c>MapAlvo</c> is therefore a <em>composition</em> of the two finer-grained calls, both of which stay
/// public — the shape <c>MapControllers</c> has beside <c>MapControllerRoute</c> — and
/// <see cref="MapAlvo_maps_exactly_what_the_two_parts_map"/> is what keeps it one.
/// </para>
/// </remarks>
public sealed class MinimalRegistrationTests
{
    /// <summary>
    /// A provider a registration-only fact can name without provisioning anything. Nothing here starts a
    /// host, so no connection is ever opened against it.
    /// </summary>
    private const string UnopenedSqlite = "Data Source=:memory:";

    private const string HostChosenRoutePrefix = "/data";

    /// <summary>
    /// The whole embedded surface, as the design promises a host may write it: one registration and one
    /// mapping, with no apply, no result to check, and no <c>AddDataApi()</c>.
    /// </summary>
    /// <remarks>
    /// Both answers are load-bearing and neither implies the other. <c>/api/vehicles</c> answering at all
    /// proves the Data API's services were registered without being asked for and that its routes
    /// materialised from a schema the boot primed <em>after</em> the mapping; readiness answering 200 proves
    /// the boot itself reached <c>Ready</c>, so an <c>/api</c> 200 produced by some other route table cannot
    /// carry this fact on its own.
    /// </remarks>
    [Fact]
    public async Task Two_calls_are_enough_for_a_working_backend()
    {
        await using var database = await SqliteApiEngine.Instance.CreateDatabaseAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAlvo(alvo => alvo
            .UseSqlite(ConnectionStringOf(database))
            .FromDescriptor(VehiclesDescriptorPath));

        await using var app = builder.Build();
        app.MapAlvo();
        await app.StartAsync(TestContext.Current.CancellationToken);

        (await GetAsync(app, "/api/vehicles")).ShouldBe(
            HttpStatusCode.OK, "the Data API must serve without AddDataApi() and without an apply of its own");
        (await GetAsync(app, AlvoHealth.ReadinessPath)).ShouldBe(
            HttpStatusCode.OK, "the boot must have primed the schema, or the 200 above came from somewhere else");
    }

    /// <summary>
    /// <c>AddDataApi()</c> is configuration, not a switch: the services it used to be asked for are
    /// registered by <c>AddAlvo</c> itself.
    /// </summary>
    [Fact]
    public void The_data_api_is_registered_without_an_explicit_AddDataApi()
    {
        using var services = Registered();

        services.GetService<EntityRouteCatalog>().ShouldNotBeNull(
            "the generated Data API is the point of the framework, so a host must not have to ask for it");
    }

    /// <summary>
    /// The verb taxonomy's <c>Add{Thing}</c> is idempotent (<c>extensibility.md</c> rule 7), and a
    /// configuration-only <c>AddDataApi</c> has to stay that way: a second call must not undo the first.
    /// </summary>
    /// <remarks>
    /// The default registration runs <em>before</em> this configuration — <c>AddAlvo</c> registers the API's
    /// services and only then invokes the caller's callback — so this is the "register, then configure" order.
    /// <see cref="Api_options_configured_before_AddAlvo_still_win"/> is the other one.
    /// </remarks>
    [Fact]
    public void AddDataApi_still_configures_and_is_idempotent()
    {
        using var services = Registered(alvo => alvo
            .AddDataApi(api => api.RoutePrefix = HostChosenRoutePrefix)
            .AddDataApi());

        RoutePrefixOf(services).ShouldBe(
            HostChosenRoutePrefix, "a second AddDataApi() is not a duplicate and must not reset the first");
    }

    /// <summary>
    /// The other order: a host that configured <see cref="AlvoApiOptions"/> before Alvo was registered at
    /// all still gets its own value.
    /// </summary>
    /// <remarks>
    /// It holds because the default registration contributes no configure action of its own — the defaults
    /// are the property initializers — so there is nothing for a later registration to overwrite. Worth a
    /// fact rather than a reading of the options pipeline: making the Data API default-on is precisely the
    /// change that could have introduced a configure action that silently won.
    /// </remarks>
    [Fact]
    public void Api_options_configured_before_AddAlvo_still_win()
    {
        var services = new ServiceCollection();
        services.Configure<AlvoApiOptions>(api => api.RoutePrefix = HostChosenRoutePrefix);
        services.AddAlvo(alvo => alvo.UseSqlite(UnopenedSqlite));

        using var provider = services.BuildServiceProvider();

        RoutePrefixOf(provider).ShouldBe(
            HostChosenRoutePrefix, "the default registration must not configure over a host's own value");
    }

    /// <summary>
    /// <c>MapAlvo()</c> is exactly its two parts — the fact that makes the composition a claim rather than a
    /// coincidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison is over every data source the mapping produced <em>and</em> the endpoints inside each,
    /// so it fails in both directions: a third thing added to <c>MapAlvo</c> and not to the parts, and a part
    /// dropped from <c>MapAlvo</c>. Comparing endpoints alone would not do — the Data API's source publishes
    /// none until a schema is primed, so its whole presence lives in the source list.
    /// </para>
    /// <para>
    /// Non-vacuity is asserted, not assumed: two hosts that mapped nothing at all also agree. The shape must
    /// carry both probe routes and the Data API's own source.
    /// </para>
    /// </remarks>
    [Fact]
    public void MapAlvo_maps_exactly_what_the_two_parts_map()
    {
        using var umbrella = Application();
        using var parts = Application();

        umbrella.MapAlvo();
        parts.MapAlvoHealth();
        parts.MapAlvoDataApi();

        var mapped = MappedShape(umbrella);
        mapped.ShouldBe(MappedShape(parts));
        RoutePatterns(umbrella).ShouldBe(
            [AlvoHealth.LivenessPath, AlvoHealth.ReadinessPath],
            ignoreOrder: true,
            "an umbrella that mapped no probe route would agree with parts that mapped none either");
        mapped.ShouldContain(
            shape => shape.StartsWith(nameof(AlvoEndpointDataSource), StringComparison.Ordinal),
            "the Data API publishes no endpoint until a schema is primed, so its source is the only sign of it");
    }

    /// <summary>
    /// Health maps <b>first</b>: a probe route exists even when the Data API's registration refuses.
    /// </summary>
    /// <remarks>
    /// The refusal is provoked the only way it can be — by taking <see cref="EntityRouteCatalog"/> back out
    /// of the collection, which is what a host that never registered Alvo's API services looks like from
    /// inside <c>MapAlvoDataApi</c>. Order is invisible in the source once both calls are there, so this is
    /// the fact that holds it: mapping health second would leave an operator with a container that cannot be
    /// probed at all, and no way to tell it from one that is merely slow to start.
    /// </remarks>
    [Fact]
    public void A_refused_data_api_still_leaves_the_probe_routes_mapped()
    {
        var builder = HostBuilder();
        builder.Services.Remove(builder.Services.Single(RegistersTheRouteCatalog));
        using var app = builder.Build();

        Should.Throw<InvalidOperationException>(app.MapAlvo);

        RoutePatterns(app).ShouldBe(
            [AlvoHealth.LivenessPath, AlvoHealth.ReadinessPath],
            ignoreOrder: true,
            "mapping health after the Data API would leave a refused host with nothing to probe");
    }

    private static bool RegistersTheRouteCatalog(ServiceDescriptor service) =>
        service.ServiceType == typeof(EntityRouteCatalog);

    /// <summary>The repository's own example descriptor, the one every route fact in this suite is written against.</summary>
    private static string VehiclesDescriptorPath =>
        Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json");

    /// <summary>
    /// The connection string a host would write, taken from the engine that provisioned the database rather
    /// than composed here — SQLite's shared-cache keep-alive has exactly one owner.
    /// </summary>
    /// <param name="database">The provisioned database.</param>
    private static string ConnectionStringOf(AlvoApiDatabase database)
    {
        using var connection = database.Connect();
        return connection.ConnectionString;
    }

    private static async Task<HttpStatusCode> GetAsync(WebApplication app, string path)
    {
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>A container holding nothing but a host's own registration of Alvo.</summary>
    /// <param name="configure">Anything the host attaches to the builder.</param>
    private static ServiceProvider Registered(Action<IAlvoBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo =>
        {
            alvo.UseSqlite(UnopenedSqlite);
            configure?.Invoke(alvo);
        });

        return services.BuildServiceProvider();
    }

    private static string RoutePrefixOf(IServiceProvider services) =>
        services.GetRequiredService<IOptions<AlvoApiOptions>>().Value.RoutePrefix;

    private static WebApplicationBuilder HostBuilder()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddAlvo(alvo => alvo.UseSqlite(UnopenedSqlite));
        return builder;
    }

    private static WebApplication Application() => HostBuilder().Build();

    /// <summary>
    /// Every endpoint data source a mapping produced, each with the endpoints it publishes — the whole
    /// mapped shape rather than a chosen part of it.
    /// </summary>
    /// <param name="app">The application that was mapped onto.</param>
    private static IReadOnlyList<string> MappedShape(WebApplication app) =>
        [.. Sources(app).Select(DescribeSource).Order(StringComparer.Ordinal)];

    /// <summary>Every route pattern the mapping published, for the facts about the probe routes themselves.</summary>
    /// <param name="app">The application that was mapped onto.</param>
    private static IReadOnlyList<string> RoutePatterns(WebApplication app) =>
        [.. Sources(app)
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Distinct(StringComparer.Ordinal)];

    private static ICollection<EndpointDataSource> Sources(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources;

    private static string DescribeSource(EndpointDataSource source) =>
        $"{source.GetType().Name}[{string.Join(", ", source.Endpoints.Select(DescribeEndpoint).Order(StringComparer.Ordinal))}]";

    private static string DescribeEndpoint(Endpoint endpoint) =>
        endpoint is RouteEndpoint route ? route.RoutePattern.RawText! : endpoint.DisplayName ?? endpoint.ToString()!;
}
