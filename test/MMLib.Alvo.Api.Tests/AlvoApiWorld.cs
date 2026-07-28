using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Tests.Data;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// One running Data API: a real <see cref="WebApplication"/> over
/// <see cref="TestServer"/>, a real SQLite database, a real descriptor applied through the
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
/// <b>The database is in-memory but <em>shared-cache</em>, not a bare <c>:memory:</c>.</b> A bare
/// <c>Data Source=:memory:</c> gives every connection its own private, empty database, and Alvo's
/// relational driver opens a fresh connection per unit of work by design
/// (<c>RelationalConnectionFactory</c>) — so the migration would create tables one connection could
/// see and no request ever could. A uniquely named <c>Mode=Memory;Cache=Shared</c> source, kept alive
/// by one open connection for the world's lifetime, is the shape that actually behaves like one
/// database while still needing no file and no container.
/// </para>
/// <para>
/// The unique database name doubles as <see cref="SqlCapture"/>'s marker, so
/// <see cref="ClearStatements"/>/<see cref="Statements"/> report the statements of <em>this</em> world
/// only and worlds stay safe to run in parallel.
/// </para>
/// </remarks>
internal sealed class AlvoApiWorld : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly WebApplication _app;
    private readonly SqlCapture _capture;
    private readonly HttpClient _client;
    private readonly string _connectionString;
    private readonly AlvoAuthOptions _authOptions;

    private AlvoApiWorld(
        SqliteConnection keepAlive,
        WebApplication app,
        SqlCapture capture,
        string connectionString,
        AlvoAuthOptions authOptions)
    {
        _keepAlive = keepAlive;
        _app = app;
        _capture = capture;
        _connectionString = connectionString;
        _authOptions = authOptions;
        _client = app.GetTestClient();
    }

    /// <summary>Starts a world over the repository's <c>examples/vehicle-registry</c> descriptor.</summary>
    /// <param name="keys">The dev API keys the world issues.</param>
    /// <param name="routePrefix">A non-default <see cref="AlvoApiOptions.RoutePrefix"/>, or <see langword="null"/> for the default.</param>
    /// <param name="revokedKeyId">The one key id whose stored record is revoked, for the 401 diagnosis that dev-key configuration cannot express.</param>
    internal static Task<AlvoApiWorld> VehicleRegistryAsync(
        IReadOnlyList<TestApiKey>? keys = null, string? routePrefix = null, string? revokedKeyId = null) =>
        StartAsync(
            Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json"),
            keys ?? [],
            routePrefix,
            revokedKeyId);

    /// <summary>Starts a world over the tenant-scoped <c>notes</c> descriptor this project ships.</summary>
    /// <param name="keys">The dev API keys the world issues.</param>
    internal static Task<AlvoApiWorld> TenantNotesAsync(IReadOnlyList<TestApiKey> keys) =>
        StartAsync(
            Path.Combine(AppContext.BaseDirectory, "descriptors", "tenant-notes.alvo.json"),
            keys,
            routePrefix: null,
            revokedKeyId: null);

    private static async Task<AlvoApiWorld> StartAsync(
        string descriptorPath, IReadOnlyList<TestApiKey> keys, string? routePrefix, string? revokedKeyId)
    {
        var databaseName = $"alvo-api-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync(TestContext.Current.CancellationToken);

        var app = BuildApp(descriptorPath, connectionString, keys, routePrefix, revokedKeyId);
        await ApplyDescriptorAsync(app);

        app.MapAlvoDataApi();
        var capture = new SqlCapture(databaseName);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var authOptions = app.Services.GetRequiredService<IOptions<AlvoAuthOptions>>().Value;
        return new AlvoApiWorld(keepAlive, app, capture, connectionString, authOptions);
    }

    /// <summary>
    /// Wires the host exactly as a consumer would, with one substitution the dev-key surface cannot
    /// express: <see cref="AlvoDevApiKey"/> has no revocation field, so a world that needs a revoked
    /// key registers a decorating <see cref="IApiKeyStore"/> <em>before</em> <c>AddAlvo</c> — the same
    /// <c>TryAdd</c> seam a host with a database-backed key store uses. Everything the dev-key surface
    /// can express goes through it, so authentication itself is never faked.
    /// </summary>
    private static WebApplication BuildApp(
        string descriptorPath,
        string connectionString,
        IReadOnlyList<TestApiKey> keys,
        string? routePrefix,
        string? revokedKeyId)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        if (revokedKeyId is not null)
        {
            builder.Services.AddSingleton<IApiKeyStore>(services => new RevokedKeyStore(
                new InMemoryApiKeyStore(services.GetRequiredService<IOptions<AlvoAuthOptions>>()), revokedKeyId));
        }

        builder.Services.Configure<AlvoAuthOptions>(options =>
        {
            foreach (var key in keys)
            {
                options.DevKeys.Add(key.ToDevKey());
            }
        });

        builder.Services.AddAlvo(alvo => alvo
            .UseSqlite(connectionString)
            .FromDescriptor(descriptorPath)
            .AddDataApi(api =>
            {
                if (routePrefix is not null)
                {
                    api.RoutePrefix = routePrefix;
                }
            }));

        return builder.Build();
    }

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
        [.. ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => Methods(endpoint).Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Order(StringComparer.Ordinal)];

    private static IEnumerable<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];

    /// <summary>Every statement the engine has run against this world's database since the last <see cref="ClearStatements"/>.</summary>
    internal IReadOnlyList<string> Statements => _capture.Statements;

    /// <summary>Forgets every recorded statement, so a fact asserts on the ones its own request produced.</summary>
    internal void ClearStatements() => _capture.Clear();

    /// <summary>Sends a request, presenting <paramref name="key"/> and <paramref name="tenant"/> the way an HTTP caller would.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The request path, including the route prefix.</param>
    /// <param name="key">The API key to present, or <see langword="null"/> to present none at all.</param>
    /// <param name="tenant">The tenant to request, or <see langword="null"/> to request none.</param>
    /// <param name="body">A JSON body to send, or <see langword="null"/> for none.</param>
    internal async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, TestApiKey? key = null, string? tenant = null, JsonObject? body = null)
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

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, new MediaTypeHeaderValue("application/json"));
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Inserts a row straight into the database, bypassing policy — a world's own seeding is not the act under test.</summary>
    /// <param name="sql">The insert statement, with <c>$name</c> placeholders.</param>
    /// <param name="parameters">The parameter values by placeholder name.</param>
    internal async Task SeedAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _capture.Dispose();
        _client.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        await _keepAlive.DisposeAsync();
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
}

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
    internal Guid User { get; } = Guid.NewGuid();

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
