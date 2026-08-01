using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Tests.Data;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// One running Data API: a real <see cref="WebApplication"/> over
/// <see cref="TestServer"/>, a real database on a real engine, a real descriptor applied through the
/// production migration flow, and real dev API keys.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="TestServer"/> rather than <c>WebApplicationFactory&lt;T&gt;</c></b>: the factory needs
/// an entry-point assembly to bootstrap, and Alvo has none until <c>MMLib.Alvo.Host</c> lands in PR4.
/// Building the host here also keeps the wiring under test identical to what an <em>embedded</em> host
/// writes — <c>AddAlvo(...).AddDataApi()</c> then <c>app.MapAlvoDataApi()</c> — which is the seam this
/// task actually ships.
/// </para>
/// <para>
/// <b>The engine is a parameter, defaulting to SQLite.</b> Everything engine-specific — provisioning the
/// database, registering the provider, opening a connection for an out-of-band read — lives behind
/// <see cref="AlvoApiEngine"/>, so the same world, the same requests and the same assertions run on
/// PostgreSQL (<c>MMLib.Alvo.Api.Tests.Integration</c>) without a second copy of any of it. SQLite is the
/// default because it needs no container and therefore keeps ring0 Docker-free.
/// </para>
/// <para>
/// The database's unique name doubles as <see cref="SqlCapture"/>'s marker, so
/// <see cref="ClearStatements"/>/<see cref="Statements"/> report the statements of <em>this</em> world
/// only and worlds stay safe to run in parallel.
/// </para>
/// </remarks>
internal sealed class AlvoApiWorld : IAsyncDisposable
{
    private readonly AlvoApiDatabase _database;
    private readonly WebApplication _app;
    private readonly SqlCapture _capture;
    private readonly HttpClient _client;
    private readonly AlvoAuthOptions _authOptions;

    private AlvoApiWorld(
        AlvoApiDatabase database,
        WebApplication app,
        SqlCapture capture,
        AlvoAuthOptions authOptions)
    {
        _database = database;
        _app = app;
        _capture = capture;
        _authOptions = authOptions;
        _client = app.GetTestClient();
    }

    /// <summary>Starts a world over the repository's <c>examples/vehicle-registry</c> descriptor.</summary>
    /// <param name="keys">The dev API keys the world issues.</param>
    /// <param name="setup">Anything the world's host is configured differently from the default.</param>
    /// <param name="engine">The engine to run on; SQLite when none is named.</param>
    internal static Task<AlvoApiWorld> VehicleRegistryAsync(
        IReadOnlyList<TestApiKey>? keys = null, AlvoApiWorldSetup? setup = null, AlvoApiEngine? engine = null) =>
        StartAsync(
            Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json"),
            keys ?? [],
            setup ?? new AlvoApiWorldSetup(),
            engine ?? SqliteApiEngine.Instance);

    /// <summary>Starts a world over the tenant-scoped <c>notes</c> descriptor this project ships.</summary>
    /// <param name="keys">The dev API keys the world issues.</param>
    internal static Task<AlvoApiWorld> TenantNotesAsync(IReadOnlyList<TestApiKey> keys) =>
        StartAsync(
            Path.Combine(AppContext.BaseDirectory, "descriptors", "tenant-notes.alvo.json"),
            keys,
            new AlvoApiWorldSetup(),
            SqliteApiEngine.Instance);

    /// <summary>Starts a world over one of this project's own descriptor fixtures.</summary>
    /// <param name="fileName">The descriptor file's name under <c>descriptors/</c>.</param>
    /// <param name="keys">The dev API keys the world issues.</param>
    /// <param name="setup">Anything the world's host is configured differently from the default.</param>
    internal static Task<AlvoApiWorld> FromDescriptorAsync(
        string fileName, IReadOnlyList<TestApiKey>? keys = null, AlvoApiWorldSetup? setup = null) =>
        StartAsync(
            Path.Combine(AppContext.BaseDirectory, "descriptors", fileName),
            keys ?? [],
            setup ?? new AlvoApiWorldSetup(),
            SqliteApiEngine.Instance);

    private static async Task<AlvoApiWorld> StartAsync(
        string descriptorPath, IReadOnlyList<TestApiKey> keys, AlvoApiWorldSetup setup, AlvoApiEngine engine)
    {
        var database = await engine.CreateDatabaseAsync();

        try
        {
            return await StartAsync(descriptorPath, keys, setup, database);
        }
        catch
        {
            // A world whose mapping is *meant* to fail is a fact of its own, so the database (and with it
            // SQLite's keep-alive connection, or PostgreSQL's created database) has to be released on that
            // path too — a leaked one holds a shared cache, or a container's disk, for the rest of the run.
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<AlvoApiWorld> StartAsync(
        string descriptorPath,
        IReadOnlyList<TestApiKey> keys,
        AlvoApiWorldSetup setup,
        AlvoApiDatabase database)
    {
        var app = BuildApp(descriptorPath, database, keys, setup);

        // Middleware ordering the compiler cannot check: UseExceptionHandler has to be added before any
        // endpoint runs, and WebApplication auto-terminates the pipeline with routing — so registering it
        // here, before MapAlvoDataApi below, is what puts it upstream of the endpoints.
        if (setup.MapAlvoProblemDetails)
        {
            app.UseExceptionHandler();
        }

        // UsePathBase with no explicit UseRouting after it, which is the whole shape an embedded host writes
        // (#121 quotes it verbatim) and therefore the only shape worth measuring. The widely cited rule that
        // WebApplication needs UseRouting *after* UsePathBase — Microsoft Learn still states it — no longer
        // holds: UsePathBaseMiddleware re-runs matching itself, so routing observes the rewritten path.
        // Measured, not assumed: a probe under this runtime answers 200 for UsePathBase and 404 for the same
        // rewrite performed by hand, and adding UseRouting here leaves every fact in this suite unchanged.
        if (setup.PathBase is { } pathBase)
        {
            app.UsePathBase(pathBase);
        }

        await ApplyDescriptorAsync(app);

        app.MapAlvoDataApi();

        // Opt-in, because MapAlvoDataApi deliberately does not map it — serving a document is a hosting
        // decision (ApiSetup.AddAlvoApi says so) — and because every route-table fact in this suite counts
        // the endpoints it finds. A world that always mapped one would silently add a sixteenth endpoint to
        // facts asserting there are fifteen, which is the kind of drift those counts exist to catch.
        if (setup.MapOpenApiDocument)
        {
            app.MapOpenApi();
        }

        var capture = new SqlCapture(database.Marker);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var authOptions = app.Services.GetRequiredService<IOptions<AlvoAuthOptions>>().Value;
        return new AlvoApiWorld(database, app, capture, authOptions);
    }

    /// <summary>
    /// Wires the host exactly as a consumer would, with two substitutions at the <c>TryAdd</c> seam a
    /// host would use anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AlvoDevApiKey"/> has no revocation field, so a world that needs a revoked key registers
    /// a decorating <see cref="IApiKeyStore"/>. Everything the dev-key surface <em>can</em> express goes
    /// through it, so authentication itself is never faked.
    /// </para>
    /// <para>
    /// The ambient accessor is wrapped in a recorder over the production one, so a fact can assert
    /// <em>what was published</em> — "an anonymous caller has no principal" is a statement about the
    /// accessor, invisible in any response.
    /// </para>
    /// </remarks>
    private static WebApplication BuildApp(
        string descriptorPath,
        AlvoApiDatabase database,
        IReadOnlyList<TestApiKey> keys,
        AlvoApiWorldSetup setup)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IAlvoContextAccessor>(new RecordingContextAccessor(new AlvoContextAccessor()));

        if (setup.RevokedKeyId is not null)
        {
            builder.Services.AddSingleton<IApiKeyStore>(services => new RevokedKeyStore(
                new InMemoryApiKeyStore(services.GetRequiredService<IOptions<AlvoAuthOptions>>()),
                setup.RevokedKeyId));
        }

        if (setup.ConfigureHostJson is not null)
        {
            builder.Services.ConfigureHttpJsonOptions(json => setup.ConfigureHostJson(json.SerializerOptions));
        }

        if (setup.MapOpenApiDocument)
        {
            // Alvo deliberately does not call AddOpenApi itself (ApiSetup.AddAlvoApi says why) — serving a
            // document, and therefore registering one at all, is a hosting decision. This is that decision,
            // made the way an embedded host would make it, and made *before* AddAlvo below: registration order
            // is document-transformer order, and the fixture's own Info has to be written before Alvo's
            // transformer decides whether to append to it or start it from nothing.
            builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info ??= new OpenApiInfo();
                document.Info.Title = FixtureDocumentTitle;
                document.Info.Version = FixtureDocumentVersion;
                if (setup.HostInfoDescription is { } description)
                {
                    document.Info.Description = description;
                }

                return Task.CompletedTask;
            }));
        }

        builder.Services.Configure<AlvoAuthOptions>(options =>
        {
            foreach (var key in keys)
            {
                options.DevKeys.Add(key.ToDevKey());
            }
        });

        if (setup.FaultingData)
        {
            builder.Services.AddSingleton<IAlvoData>(new FaultingAlvoData());
        }

        if (setup.MapAlvoProblemDetails)
        {
            builder.Services.AddAlvoProblemDetails();
        }

        builder.Services.AddAlvo(alvo =>
        {
            // The provider registration is the engine's, through the same public extension a host calls
            // (UseSqlite / UsePostgreSql) — never a DbContextOptions this fixture built itself, which is what
            // would let a world pass while the production registration was broken.
            database.Use(alvo);
            alvo.FromDescriptor(descriptorPath).AddDataApi(setup.ConfigureApi ?? (_ => { }));
        });

        return builder.Build();
    }

    /// <summary>
    /// The fixture host's own <c>info.title</c>, pinned rather than left to ASP.NET's default.
    /// </summary>
    /// <remarks>
    /// The unconfigured default is the <em>host assembly's</em> name, which would move
    /// <c>OpenApiDocumentTests.The_document_is_stable</c>'s baseline the moment the test project itself was
    /// renamed — a change with nothing to do with the document this feature publishes. A literal here also
    /// doubles as the fact that Alvo appends to a host's <c>info</c> rather than replacing it: the title
    /// survives untouched, and only <c>description</c> gains Alvo's own paragraph.
    /// </remarks>
    private const string FixtureDocumentTitle = "Alvo API Tests Fixture";

    private const string FixtureDocumentVersion = "v1";

    /// <summary>
    /// Runs the production code-first migration flow, which is also what primes the policy catalog and
    /// therefore the applied schema the routes are generated from. A world whose descriptor did not
    /// apply would map no routes at all, so the result is asserted here rather than in every fact.
    /// </summary>
    private static async Task ApplyDescriptorAsync(WebApplication app)
    {
        var result = await app.Services.GetRequiredService<SchemaMigrationRunner>()
            .RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue("the world's descriptor must apply, or no route is generated at all");
    }

    /// <summary>Every route the host has mapped, as <c>METHOD pattern</c> — the mapped set itself, not a guess at it.</summary>
    /// <remarks>
    /// Read off <see cref="IEndpointRouteBuilder.DataSources"/>, which is the public surface the
    /// mapping wrote into; asking the container for an <c>EndpointDataSource</c> would answer with an
    /// empty sequence when nothing registered one, and a fact over an empty sequence proves nothing.
    /// </remarks>
    internal IReadOnlyList<string> Routes =>
        [.. Endpoints
            .SelectMany(endpoint => Methods(endpoint).Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every mapped endpoint, for the facts that assert on an endpoint's <em>metadata</em> rather than on
    /// the response it produces.
    /// </summary>
    internal IReadOnlyList<RouteEndpoint> Endpoints =>
        [.. ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()];

    private static IEnumerable<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];

    /// <summary>
    /// The OpenAPI document this world serves, as the exact bytes a client receives.
    /// </summary>
    /// <remarks>
    /// Fetched over HTTP rather than composed from the container, because the document <em>is</em> the published
    /// contract and a fact about it must measure what a caller gets. It requires
    /// <see cref="AlvoApiWorldSetup.MapOpenApiDocument"/>, and says so rather than answering with a 404 body a
    /// fact would then assert over.
    /// </remarks>
    internal async Task<string> OpenApiTextAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "/openapi/v1.json");
        var text = await response.ReadTextAsync();
        return response.StatusCode == System.Net.HttpStatusCode.OK
            ? text
            : throw new InvalidOperationException(
                $"The world served {(int)response.StatusCode} for its OpenAPI document. Start it with "
                + $"AlvoApiWorldSetup(MapOpenApiDocument: true). Body: {text}");
    }

    /// <summary>The same document, parsed — for the facts that assert on its structure rather than its bytes.</summary>
    internal async Task<JsonObject> OpenApiDocumentAsync() =>
        JsonNode.Parse(await OpenApiTextAsync()) as JsonObject
        ?? throw new InvalidOperationException("The OpenAPI document is not a JSON object.");

    /// <summary>
    /// This world's container, for the facts whose claim is about what a host's <em>registrations</em> are
    /// rather than about a response — "<c>AddAlvo</c> registered no exception handler" is invisible on the wire.
    /// </summary>
    internal IServiceProvider Services => _app.Services;

    /// <summary>Every statement the engine has run against this world's database since the last <see cref="ClearStatements"/>.</summary>
    internal IReadOnlyList<string> Statements => _capture.Statements;

    /// <summary>Forgets every recorded statement, so a fact asserts on the ones its own request produced.</summary>
    internal void ClearStatements() => _capture.Clear();

    /// <summary>
    /// How many rows a table holds, read straight from this world's database rather than through the API.
    /// </summary>
    /// <remarks>
    /// "No row was written, and the table still exists" is the load-bearing half of an injection fact, and it
    /// cannot be asked of the endpoint under test: a caller's policy already hides rows, so a list that came
    /// back empty proves nothing about the table. This goes around the API on purpose. The table name is a
    /// literal supplied by the fact, never by generated input.
    /// </remarks>
    /// <param name="table">The table to count. A literal supplied by the fact, never caller or generated input.</param>
    internal Task<long> CountRowsAsync(string table) => ExecuteCountAsync(BuildCountCommand(table));

    /// <summary>
    /// How many rows in <paramref name="table"/> have <paramref name="column"/> equal to <paramref name="value"/>,
    /// read straight from this world's database.
    /// </summary>
    /// <remarks>
    /// <paramref name="value"/> is bound as a parameter rather than interpolated into the command text. Every
    /// call site today passes a literal the fact itself supplies, but a helper whose whole purpose is to be
    /// trusted about row counts should not carry a string-interpolation seam even so.
    /// </remarks>
    /// <param name="table">The table to count. A literal supplied by the fact.</param>
    /// <param name="column">The column to compare. A literal supplied by the fact.</param>
    /// <param name="value">The value <paramref name="column"/> must equal.</param>
    internal Task<long> CountRowsAsync(string table, string column, string value) =>
        ExecuteCountAsync(BuildCountCommand(table, column, value));

    private DbCommand BuildCountCommand(string table)
    {
        var command = _database.Connect().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return command;
    }

    private DbCommand BuildCountCommand(string table, string column, string value)
    {
        var command = _database.Connect().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" = @value";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@value";
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return command;
    }

    private static async Task<long> ExecuteCountAsync(DbCommand count)
    {
        await using var connection = count.Connection!;
        using var command = count;
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // Both engines answer COUNT(*) as a 64-bit integer, but they do not agree on the CLR type they
        // materialize it as (SQLite long, Npgsql long as well — but a cast would silently start failing if
        // either changed), so it is converted rather than cast.
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), Culture);
    }

    /// <summary>
    /// This world's database, for the facts that have to reach it directly — a bulk seed that would be the
    /// test rather than the setup if it went through the API, request by request.
    /// </summary>
    internal AlvoApiDatabase Database => _database;

    private static System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// Every principal this world's ambient accessor has been asked to publish, in order —
    /// <see langword="null"/> entries included, since those are the clears.
    /// </summary>
    internal IReadOnlyList<AlvoPrincipal?> PublishedPrincipals =>
        ((RecordingContextAccessor)_app.Services.GetRequiredService<IAlvoContextAccessor>()).Published;

    /// <summary>Sends a request, presenting <paramref name="key"/> and <paramref name="tenant"/> the way an HTTP caller would.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The request path, including the route prefix.</param>
    /// <param name="key">The API key to present, or <see langword="null"/> to present none at all.</param>
    /// <param name="tenant">The tenant to request, or <see langword="null"/> to request none.</param>
    /// <param name="body">A JSON body to send, or <see langword="null"/> for none.</param>
    /// <param name="headers">Any further request headers to present, by name.</param>
    internal Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        TestApiKey? key = null,
        string? tenant = null,
        JsonObject? body = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null) =>
        SendRawAsync(
            method, path, key, tenant, body is null ? null : JsonContent.Create(body, JsonMediaType), headers);

    /// <summary>
    /// Sends a request with a body this world does not serialize for the caller — for the facts about
    /// bodies the API must <em>refuse</em>, which a typed <see cref="JsonObject"/> cannot express.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The request path, including the route prefix.</param>
    /// <param name="key">The API key to present, or <see langword="null"/> to present none at all.</param>
    /// <param name="tenant">The tenant to request, or <see langword="null"/> to request none.</param>
    /// <param name="content">The body to send verbatim, or <see langword="null"/> for none.</param>
    /// <param name="headers">
    /// Any further request headers to present, by name — added <em>without validation</em>, which is the
    /// whole point: a fact about a malformed <c>If-Match</c> cannot be written through a client that refuses
    /// to send one.
    /// <para>
    /// A sequence of pairs rather than a dictionary, so a fact can present <b>the same header twice</b>: two
    /// field lines is a distinct request from one line carrying a comma, and for a header that names one thing
    /// (an idempotency key) the duplicate is an ambiguity the API has to refuse. A dictionary cannot express it
    /// at all, and a branch no fact can reach is a branch nothing holds.
    /// </para>
    /// </param>
    internal async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        string path,
        TestApiKey? key = null,
        string? tenant = null,
        HttpContent? content = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(_authOptions.HeaderName, key.Presented);
        }

        if (tenant is not null)
        {
            request.Headers.TryAddWithoutValidation(_authOptions.TenantHeaderName, tenant);
        }

        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value).ShouldBeTrue(
                $"the world must really present '{name}', or the fact below measures a request it never sent");
        }

        request.Content = content;
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        await EnsureNothingInternalLeakedAsync(response);
        return response;
    }

    /// <summary>
    /// Screens <b>every</b> response the whole API suite produces for text no caller may ever be shown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than in a fact of its own, because both claims are global and a per-fact assertion only
    /// covers the responses somebody remembered to check. Both entries were live defects, not hypotheticals:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///   <c>filter-beyond-port-limits</c> is documented as a defect report the parser's own accounting makes
    ///   unreachable. A caller <em>could</em> trigger it (256 filter parameters), and the fact that was offered
    ///   as evidence could not fire. This is what "unreachable" now means: no response in the suite carries it.
    ///   </item>
    ///   <item>
    ///   <c>(Parameter '…')</c> is what <see cref="ArgumentException"/> appends to a message. It shipped in 422
    ///   bodies because the method meant to strip it cut at a newline the suffix is not behind.
    ///   </item>
    /// </list>
    /// <para>
    /// The body is read here and again by whichever reader a fact uses; <c>HttpClient</c> buffers a
    /// <c>TestServer</c> response, so repeated reads are the same bytes.
    /// </para>
    /// </remarks>
    private static async Task EnsureNothingInternalLeakedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var internalDetail in _neverInAResponse)
        {
            body.ShouldNotContain(
                internalDetail,
                Case.Sensitive,
                $"a {(int)response.StatusCode} response carried '{internalDetail}', which no caller may see");
        }
    }

    /// <summary>Text that reaching a caller is a defect, whichever endpoint produced the response.</summary>
    private static readonly string[] _neverInAResponse = ["filter-beyond-port-limits", "(Parameter '"];

    /// <summary>A raw JSON body, sent exactly as written.</summary>
    /// <param name="json">The body text.</param>
    internal static StringContent RawJson(string json) => new(json, _mediaTypeEncoding, JsonMediaTypeName);

    private const string JsonMediaTypeName = "application/json";

    private static readonly System.Text.Encoding _mediaTypeEncoding = System.Text.Encoding.UTF8;

    private static MediaTypeHeaderValue JsonMediaType => new(JsonMediaTypeName);

    public async ValueTask DisposeAsync()
    {
        _capture.Dispose();
        _client.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    /// <summary>
    /// Stamps <see cref="ApiKeyRecord.RevokedAt"/> onto one key id and otherwise delegates, so the 401
    /// under test is produced by the production usability check (<see cref="ApiKeyRecord.IsUsable"/>)
    /// over a key that authenticates correctly — not by a store that simply refuses to find it, which
    /// is the *unknown*-key diagnosis and a different fact.
    /// </summary>
    private sealed class RevokedKeyStore(IApiKeyStore inner, string revokedKeyId) : IApiKeyStore
    {
        public async ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken)
        {
            var record = await inner.FindAsync(keyId, cancellationToken);
            return record is not null && string.Equals(keyId, revokedKeyId, StringComparison.Ordinal)
                ? record with { RevokedAt = DateTimeOffset.UnixEpoch }
                : record;
        }

        public ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken cancellationToken) =>
            inner.TouchAsync(keyId, usedAt, cancellationToken);
    }

    /// <summary>
    /// Records every publish while delegating to the production accessor, so a fact can assert what was
    /// published rather than only what the response said. "An anonymous caller has no principal" is a
    /// statement about this seam and is invisible in any response body.
    /// </summary>
    private sealed class RecordingContextAccessor(IAlvoContextAccessor inner) : IAlvoContextAccessor
    {
        private readonly List<AlvoPrincipal?> _published = [];

        internal IReadOnlyList<AlvoPrincipal?> Published
        {
            get
            {
                lock (_published)
                {
                    return [.. _published];
                }
            }
        }

        public AlvoPrincipal? Principal
        {
            get => inner.Principal;
            set
            {
                lock (_published)
                {
                    _published.Add(value);
                }

                inner.Principal = value;
            }
        }
    }
}

/// <summary>Everything an <see cref="AlvoApiWorld"/> may be configured differently from the default.</summary>
/// <param name="ConfigureApi">Configures <see cref="AlvoApiOptions"/> — the route prefix, the paging defaults, the payload bounds.</param>
/// <param name="RevokedKeyId">The one key id whose stored record is revoked, for the 401 diagnosis dev-key configuration cannot express.</param>
/// <param name="ConfigureHostJson">
/// Configures the <em>host's</em> JSON options, for the facts that a host's serializer settings cannot
/// move Alvo's wire contract.
/// </param>
/// <param name="MapOpenApiDocument">
/// Whether the world serves the OpenAPI document at <c>/openapi/v1.json</c>. Off by default: Alvo's mapping
/// seam deliberately does not map it, and a world that always did would add an endpoint to every fact in this
/// suite that counts the route table.
/// </param>
/// <param name="HostInfoDescription">
/// A description to write onto <c>info.description</c> before Alvo's own transformer runs — the fixture
/// playing the part of a host that already documents itself. <see langword="null"/> leaves <c>info</c>
/// exactly as <see cref="FixtureDocumentTitle"/>/<see cref="FixtureDocumentVersion"/> set it, which is what
/// every fact except the append-not-overwrite one needs.
/// </param>
/// <param name="MapAlvoProblemDetails">
/// Whether the world calls <c>AddAlvoProblemDetails()</c> and <c>UseExceptionHandler()</c> — the standalone
/// host's decision, which <c>AddAlvo</c> deliberately does not make for an embedded one (#119). Off by
/// default, so the suite's ordinary worlds still let a broken invariant propagate the way an embedded host
/// sees it.
/// </param>
/// <param name="FaultingData">
/// Whether <see cref="MMLib.Alvo.Data.IAlvoData"/> is <see cref="FaultingAlvoData"/> instead of the engine's
/// own store — the only way to reach the port's fifth failure family, which no well-formed request can.
/// </param>
/// <param name="PathBase">
/// The path base the world is served under — <c>app.UsePathBase(...)</c>, the embedded shape #121 names —
/// or <see langword="null"/> for a host mounted at the root, which is every other fact here.
/// </param>
internal sealed record AlvoApiWorldSetup(
    Action<AlvoApiOptions>? ConfigureApi = null,
    string? RevokedKeyId = null,
    Action<System.Text.Json.JsonSerializerOptions>? ConfigureHostJson = null,
    bool MapOpenApiDocument = false,
    string? HostInfoDescription = null,
    bool MapAlvoProblemDetails = false,
    bool FaultingData = false,
    string? PathBase = null);

/// <summary>One dev API key a world issues, in the shape a test reads best.</summary>
/// <param name="KeyId">The key's public identifier.</param>
/// <param name="Roles">The role names the key grants.</param>
/// <param name="Scopes">The <c>&lt;entity|*&gt;:&lt;read|write&gt;</c> scopes the key grants.</param>
/// <param name="Tenant">The tenant the key is issued for, if any.</param>
internal sealed record TestApiKey(
    string KeyId, IReadOnlyList<string> Roles, IReadOnlyList<string> Scopes, Guid? Tenant = null)
{
    private const string Secret = "s3cret-value";

    /// <summary>The credential as a caller presents it: <c>&lt;keyId&gt;.&lt;secret&gt;</c>.</summary>
    internal string Presented => $"{KeyId}.{Secret}";

    /// <summary>The user this key authenticates as; stable per key id so an audit column is predictable.</summary>
    /// <remarks>
    /// Settable, for the one fact that needs <b>one user in two tenants</b>: an idempotency record's identity is
    /// the tenant <em>and</em> the acting user, so two keys with two users prove nothing about the tenant half —
    /// dropping the tenant from that scope leaves two distinct scopes anyway and
    /// <c>IdempotencyTests.Two_tenants_may_use_the_same_key_without_colliding</c> would pass while the tenant
    /// was ignored. Sharing the user is what makes the tenant the only thing keeping the two keys apart.
    /// </remarks>
    internal Guid User { get; init; } = Guid.NewGuid();

    internal AlvoDevApiKey ToDevKey()
    {
        var key = new AlvoDevApiKey { KeyId = KeyId, Secret = Secret, User = User, Tenant = Tenant };
        foreach (var role in Roles)
        {
            key.Roles.Add(role);
        }

        foreach (var scope in Scopes)
        {
            key.Scopes.Add(scope);
        }

        return key;
    }
}
