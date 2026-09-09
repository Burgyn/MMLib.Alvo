using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Samples.EmbeddedHost;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration;

/// <summary>
/// The embedded sample, proved rather than described.
/// </summary>
/// <remarks>
/// #24's Definition of Done is <em>"both modes start up the same functional backend from the same
/// descriptor"</em>, and prose cannot hold that. <see cref="Both_modes_generate_the_same_routes"/> is the
/// one fact that turns it into a check; the rest establish that the sample's two surfaces actually work,
/// because a backend that starts and refuses everything would satisfy a route comparison on its own.
/// </remarks>
public class EmbeddedSampleTests
{
    /// <summary>The dev key's secret, which neither host has a default for.</summary>
    private const string Secret = "sample-secret-long-enough-for-the-floor";

    /// <summary>The credential the generated Data API reads.</summary>
    private const string ApiKeyHeader = "X-Alvo-Api-Key";

    /// <summary>The roles a caller would try to grant itself, if the sign-in read them from the body.</summary>
    private static readonly string[] _forgedRoles = ["admin"];

    /// <summary>The descriptor both modes serve — the one the root compose mounts into the image.</summary>
    private static string DescriptorPath => Path.Combine(
        RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json");

    /// <summary>
    /// It starts, and the descriptor applied. A sample that does not boot is worse than no sample, and
    /// readiness is the only thing that separates "the process is up" from "the schema is up".
    /// </summary>
    [Fact]
    public async Task The_sample_boots_and_reports_ready()
    {
        await using var sample = await SampleWorld.StartAsync();

        using var response = await sample.Client.GetAsync(
            "/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// <b>The Definition of Done, mechanised.</b> The embedded sample and the standalone host, over the
    /// same descriptor file, generate the same Data API routes — the same methods on the same paths, modulo
    /// each host's own route prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A comparison, not an assertion.</b> The expected set is the <em>other mode's</em> answer rather
    /// than a list this suite wrote down: a literal list could not notice both modes losing the same route,
    /// and it is the equality of the two that #24 actually claims. It is why the sample serves
    /// <c>examples/vehicle-registry/vehicles.alvo.json</c> and not a descriptor of its own.
    /// </para>
    /// <para>
    /// <b>Method and path, not path alone.</b> Ten routes per entity live on four paths, so comparing paths
    /// would not notice a missing <c>PUT</c> or a batch verb. The count is pinned from outside as well, so
    /// the equality below cannot be satisfied by two empty sets — the failure mode a comparison of two
    /// generated things always has.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Both_modes_generate_the_same_routes()
    {
        await using var sample = await SampleWorld.StartAsync();
        await using var standalone = await StandaloneWorld.StartAsync();

        var embedded = DataApiRoutes(sample.Routes, "/api/alvo");
        var single = DataApiRoutes(standalone.Routes, "/api");

        single.Count.ShouldBe(
            30,
            "three entities in vehicles.alvo.json, ten routes each — pinned from outside, or the equality "
            + "below is satisfied by two empty sets");
        embedded.ShouldBe(single, "the same descriptor must produce the same backend in both modes");
    }

    /// <summary>
    /// The host's own surface, and the descriptor deciding on it: a cookie user holding <c>inspector</c>
    /// may repaint a vehicle and a plain authenticated one may not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the sample's whole point.</b> <c>vehicles.update</c> in the descriptor reads
    /// <c>'admin' in @user.roles || 'inspector' in @user.roles</c>, and the sample contains no
    /// authorization code at all — so the refusal below is the <em>descriptor's</em>, reached through an
    /// <c>AlvoContext</c> the sample built from a cookie. The positive half runs first, because a refusal
    /// is also what a broken endpoint produces.
    /// </para>
    /// <para>
    /// <b>The refusal is a 404, not a 403, and that is the more interesting half.</b> The rule renders to a
    /// row-level <c>USING</c> predicate, so for a caller it excludes the row is not <em>forbidden</em> — it
    /// is <em>invisible</em>, and <c>AlvoProblemTypes.NotFound</c>'s own remarks say the two are
    /// deliberately indistinguishable. A sample that asserted 403 here would be teaching a shape the
    /// framework does not have.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_hosts_own_endpoints_are_gated_by_the_descriptors_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sample = await SampleWorld.StartAsync();
        var vehicleId = await sample.SeedAVehicleAsync();

        using var inspector = await sample.SignedInAsync("inspector");
        using var repainted = await inspector.PatchAsJsonAsync(
            $"/app/vehicles/{vehicleId}", new RepaintRequest("red"), ct);
        repainted.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var plain = await sample.SignedInAsync("clerk");
        using var refused = await plain.PatchAsJsonAsync(
            $"/app/vehicles/{vehicleId}", new RepaintRequest("blue"), ct);
        refused.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "vehicles.update admits admin or inspector; for anyone else the rule makes the row invisible "
            + "rather than announcing a refusal, which is the same 404 an absent row earns");

        using var read = await plain.GetAsync("/app/vehicles", ct);
        read.StatusCode.ShouldBe(
            HttpStatusCode.OK, "vehicles.list admits any authenticated caller, so the same user may read");
    }

    /// <summary>
    /// The development sign-in refuses a user it does not know, so a caller cannot name its own identity —
    /// and it grants no role the caller asked for, because it takes no role list at all.
    /// </summary>
    /// <remarks>
    /// <b>This fact exists because the endpoint had the other shape first.</b> A sign-in that accepted a
    /// role list is an unauthenticated privilege-escalation endpoint, and a sample is a specification of the
    /// pattern people copy — so the refusal is pinned rather than left to the endpoint's comment.
    /// </remarks>
    [Fact]
    public async Task The_development_sign_in_does_not_let_a_caller_choose_its_own_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sample = await SampleWorld.StartAsync();
        using var client = sample.WithoutCredentials();

        using var unknown = await client.PostAsJsonAsync("/app/login", new LoginRequest("root"), ct);
        using var forged = await client.PostAsJsonAsync(
            "/app/login", new { User = "clerk", Roles = _forgedRoles }, ct);

        unknown.StatusCode.ShouldBe(
            HttpStatusCode.NotFound, "only the demo users this app declares may be signed in as");
        forged.StatusCode.ShouldBe(
            HttpStatusCode.OK, "an extra member is ignored rather than refused, which is the risk");
        forged.Headers.GetValues("Set-Cookie").ShouldNotBeEmpty();

        // And the cookie it issued is the clerk's, not an admin's: the roles came from the sample's table.
        var cookie = forged.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        using var asClerk = sample.WithoutCredentials();
        asClerk.DefaultRequestHeaders.Add("Cookie", cookie);
        var vehicleId = await sample.SeedAVehicleAsync();

        using var refused = await asClerk.PatchAsJsonAsync(
            $"/app/vehicles/{vehicleId}", new RepaintRequest("gold"), ct);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "the request asked for 'admin' and got the clerk's roles, so vehicles.update still excludes it");
    }

    /// <summary>
    /// The development sign-in is <b>not mapped at all</b> outside Development. It issues a cookie with no
    /// credential of any kind, so its absence everywhere else is the control that makes it safe to ship in
    /// a file people copy — and a comment saying "development only" is not a control.
    /// </summary>
    /// <remarks>
    /// <b>Staging is asserted, not only Production, because the predicate first said
    /// <c>!IsProduction()</c>.</b> That maps a credential-free sign-in somewhere real people reach, in a
    /// file whose whole purpose is to be copied — so the environment that would have slipped through is
    /// the one worth naming here.
    /// </remarks>
    /// <param name="environment">An environment the sign-in must not be reachable in.</param>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task The_development_sign_in_is_not_mapped_outside_development(string environment)
    {
        await using var sample = await SampleWorld.StartAsync(environment);
        using var client = sample.WithoutCredentials();

        using var response = await client.PostAsJsonAsync(
            "/app/login", new LoginRequest("inspector"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>An unauthenticated caller sees no data on either surface.</summary>
    /// <remarks>
    /// <para>
    /// <b>Three different answers, and the differences are the design rather than an inconsistency.</b> The
    /// app's own route is refused by <em>its</em> cookie scheme, before Alvo is reached at all. On the
    /// generated surface a <em>write</em> is refused — <c>vehicles.create</c> reads
    /// <c>'admin' in @user.roles</c>, which an anonymous caller's <c>WITH CHECK</c> fails — while a
    /// <em>read</em> answers <b>200 with an empty page</b>: a configured rule resolves to an allow carrying
    /// a <c>USING</c> predicate, and for this caller that predicate matches no row. A caller who is shown
    /// nothing and a caller who is told "no" are both denied; only one of them learns whether the entity has
    /// rows at all.
    /// </para>
    /// <para>
    /// The row count is asserted rather than the status alone, because a 200 proves nothing about
    /// visibility on its own — and the seeded vehicle is what makes the empty page a statement.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_anonymous_caller_sees_no_data_on_either_surface()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sample = await SampleWorld.StartAsync();
        await sample.SeedAVehicleAsync();

        using var anonymous = sample.WithoutCredentials();
        using var own = await anonymous.GetAsync("/app/vehicles", ct);
        // A complete body on the simplest entity, deliberately: a partial one earns a 422 from validation
        // before the policy is ever consulted, which would prove nothing about default-deny.
        using var write = await anonymous.PostAsJsonAsync(
            "/api/alvo/owners", new Dictionary<string, object?> { ["name"] = "Forged Ltd" }, ct);
        using var read = await anonymous.GetAsync("/api/alvo/vehicles", ct);

        own.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Found);
        write.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "owners.create admits admin only");
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await read.Content.ReadFromJsonAsync<JsonObject>(ct))!["items"]!.AsArray().Count.ShouldBe(
            0, "a row exists, and vehicles.list admits only an authenticated caller — so this page is empty");
    }

    /// <summary>Alvo's own surface serves an API-key caller.</summary>
    [Fact]
    public async Task The_generated_data_api_serves_an_api_key_caller()
    {
        await using var sample = await SampleWorld.StartAsync();

        using var response = await sample.AsAgent().GetAsync(
            "/api/alvo/vehicles", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The <c>Content-Type</c> guard is on here, and this suite is where the end-to-end statement belongs:
    /// the sample is the context #191's threat model is actually about — Alvo inside a host that
    /// authenticates with cookies.
    /// </summary>
    [Fact]
    public async Task A_non_json_write_is_refused_in_the_sample()
    {
        await using var sample = await SampleWorld.StartAsync();
        using var content = new StringContent("{}", Encoding.UTF8, "text/plain");

        using var response = await sample.AsAgent().PostAsync(
            "/api/alvo/vehicles", content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        response.Headers.GetValues("Accept-Post").ShouldHaveSingleItem().ShouldBe("application/json");
    }

    /// <summary>Every generated Data API route under <paramref name="prefix"/>, as <c>METHOD path</c>.</summary>
    /// <remarks>
    /// The prefix is stripped so the two modes are compared on what they generate rather than on where each
    /// mounts it — the sample deliberately mounts under <c>/api/alvo</c> to show Alvo sitting beside a
    /// host's own routes, and that difference is configuration, not backend.
    /// </remarks>
    /// <param name="endpoints">One host's endpoints.</param>
    /// <param name="prefix">That host's Data API route prefix.</param>
    private static SortedSet<string> DataApiRoutes(IEnumerable<Endpoint> endpoints, string prefix) =>
        [.. from endpoint in endpoints.OfType<RouteEndpoint>()
            let pattern = "/" + endpoint.RoutePattern.RawText!.TrimStart('/')
            where pattern.StartsWith($"{prefix}/", StringComparison.Ordinal)
            from method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []
            select $"{method} {pattern[prefix.Length..]}"];

    /// <summary>The embedded sample, running on <c>TestServer</c> over a database of its own.</summary>
    /// <remarks>
    /// Composed through <see cref="SampleHost.CreateBuilder"/> and <see cref="SampleHost.Build"/>, so what
    /// is under test is the sample's own composition. A fixture that assembled its own pipeline would go on
    /// passing after the sample stopped calling <c>MapAlvoDataApi</c>.
    /// </remarks>
    private sealed class SampleWorld : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _databasePath;

        private SampleWorld(WebApplication app, string databasePath)
        {
            _app = app;
            _databasePath = databasePath;
            Client = app.GetTestClient();
        }

        internal HttpClient Client { get; }

        internal IEnumerable<Endpoint> Routes =>
            _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        /// <summary>Starts the sample.</summary>
        /// <param name="environment">
        /// Which environment to run as, passed as <c>--environment</c> because the host reads it before any
        /// configuration source this fixture could add. It defaults to <c>Development</c>, which is what
        /// makes the development sign-in reachable at all —
        /// <see cref="The_development_sign_in_is_not_mapped_outside_development"/> is the other half.
        /// </param>
        internal static async Task<SampleWorld> StartAsync(string environment = "Development")
        {
            var databasePath = TempDatabasePath("sample");
            var builder = SampleHost.CreateBuilder(
                ["--environment", environment],
                configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Alvo:Auth:DevKeys:0:Secret"] = Secret,
                    ["FleetDesk:DatabasePath"] = databasePath,
                    ["FleetDesk:DescriptorPath"] = DescriptorPath,
                }));
            builder.WebHost.UseTestServer();

            var app = SampleHost.Build(builder);
            await app.StartAsync(TestContext.Current.CancellationToken);

            return new SampleWorld(app, databasePath);
        }

        /// <summary>A client holding this app's sign-in cookie for one of its demo users.</summary>
        /// <remarks>
        /// The user is named and its roles come from the sample's own table — the sign-in takes no role list
        /// from the caller, which is the point of that endpoint's shape and therefore of this helper's.
        /// </remarks>
        /// <param name="user">The demo user's name: <c>inspector</c> or <c>clerk</c>.</param>
        internal async Task<HttpClient> SignedInAsync(string user)
        {
            var client = _app.GetTestServer().CreateClient();
            using var response = await client.PostAsJsonAsync(
                "/app/login", new LoginRequest(user), TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();

            var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
            client.DefaultRequestHeaders.Add("Cookie", cookie);

            return client;
        }

        /// <summary>A client presenting the dev API key, for the generated surface.</summary>
        internal HttpClient AsAgent()
        {
            Client.DefaultRequestHeaders.Remove(ApiKeyHeader);
            Client.DefaultRequestHeaders.Add(ApiKeyHeader, $"agent.{Secret}");

            return Client;
        }

        /// <summary>
        /// A client of its own with no credential and no cookie — never the shared one, which
        /// <see cref="AsAgent"/> has by then given an API key.
        /// </summary>
        internal HttpClient WithoutCredentials() => _app.GetTestServer().CreateClient();

        /// <summary>
        /// One owner and one vehicle, created through the <em>generated</em> surface as the admin key.
        /// </summary>
        /// <remarks>
        /// Deliberately through <c>/api/alvo</c> rather than through a seeding hook: <c>vehicles.create</c>
        /// admits <c>admin</c> only, so this is the two-surface story doing real work — the agent surface
        /// sets up what the cookie surface then acts on.
        /// </remarks>
        internal async Task<Guid> SeedAVehicleAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            var agent = AsAgent();

            using var owner = await agent.PostAsJsonAsync(
                "/api/alvo/owners", new Dictionary<string, object?> { ["name"] = "Fleet Desk Ltd" }, ct);
            owner.StatusCode.ShouldBe(HttpStatusCode.Created);
            var ownerId = await IdOfAsync(owner);

            using var vehicle = await agent.PostAsJsonAsync(
                "/api/alvo/vehicles",
                new Dictionary<string, object?>
                {
                    ["vin"] = "1HGCM82633A004352",
                    ["plate"] = "BA-123AB",
                    ["make"] = "Skoda",
                    ["model"] = "Octavia",
                    ["year"] = 2021,
                    ["owner_id"] = ownerId,
                },
                ct);
            vehicle.StatusCode.ShouldBe(HttpStatusCode.Created);

            return await IdOfAsync(vehicle);
        }

        /// <summary>The <c>id</c> of a created row.</summary>
        /// <remarks>
        /// Read out of a <see cref="JsonObject"/> rather than a typed dictionary: a row is a heterogeneous
        /// map — strings, an integer year, nulls — so no single <c>Dictionary&lt;string, T&gt;</c> fits it.
        /// </remarks>
        /// <param name="response">The 201 to read.</param>
        private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
        {
            var row = await response.Content.ReadFromJsonAsync<JsonObject>(
                TestContext.Current.CancellationToken);

            return row!["id"]!.GetValue<Guid>();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            TryDelete(_databasePath);
        }
    }

    /// <summary>The standalone host, running on <c>TestServer</c> over the same descriptor.</summary>
    /// <remarks>
    /// Composed through <c>AlvoHost.CreateBuilder</c>/<c>BuildAsync</c> — its own public seam, and the same
    /// arrangement <c>AlvoHostWorld</c> uses — so the comparison is between two real compositions rather
    /// than between two hand-built pipelines.
    /// </remarks>
    private sealed class StandaloneWorld : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _databasePath;

        private StandaloneWorld(WebApplication app, string databasePath)
        {
            _app = app;
            _databasePath = databasePath;
        }

        internal IEnumerable<Endpoint> Routes =>
            _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        internal static async Task<StandaloneWorld> StartAsync()
        {
            var databasePath = TempDatabasePath("standalone");
            var builder = Alvo.Host.AlvoHost.CreateBuilder(
                [],
                configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Alvo:DescriptorPath"] = DescriptorPath,
                    ["Alvo:Database:Provider"] = "sqlite",
                    ["Alvo:Database:SqliteConnectionString"] = $"Data Source={databasePath}",
                    ["Alvo:Auth:DevKeys:0:KeyId"] = "agent",
                    ["Alvo:Auth:DevKeys:0:Secret"] = Secret,
                    ["Alvo:Auth:DevKeys:0:User"] = "9f1d3c7e-5b2a-4f18-8c6d-2e7a9b4c1d05",
                    ["Alvo:Auth:DevKeys:0:Roles:0"] = "admin",
                    ["Alvo:Auth:DevKeys:0:Scopes:0"] = "*:read",
                }));
            builder.WebHost.UseTestServer();

            var app = await Alvo.Host.AlvoHost.BuildAsync(builder);
            await app.StartAsync(TestContext.Current.CancellationToken);

            return new StandaloneWorld(app, databasePath);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            TryDelete(_databasePath);
        }
    }

    /// <summary>A fresh SQLite path under the temp directory.</summary>
    /// <param name="mode">Which host owns it, so a leftover file says which run left it.</param>
    private static string TempDatabasePath(string mode) =>
        Path.Combine(Path.GetTempPath(), $"alvo-embedded-sample-{mode}-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Teardown hygiene, never an assertion: a file left in the temp directory is untidy, not a failed
    /// fact, and a delete behaves differently on POSIX and Windows.
    /// </summary>
    /// <param name="databasePath">The database to remove.</param>
    private static void TryDelete(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(databasePath);
        }
        catch (IOException)
        {
        }
    }
}
