using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.Secrets;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Samples.FieldService.Tests;

/// <summary>
/// The host's routes over <see cref="TestServer"/>: the same three composition steps <c>FieldServiceHost</c> makes, on an
/// in-memory SQLite database instead of PostgreSQL, with the caller's dev keys and the technician app's signing secret.
/// </summary>
internal sealed class HttpWorld : IAsyncDisposable
{
    public const string SigningSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    private const string KeySecret = "s3cret-value-long-enough-for-the-floor";

    /// <summary>
    /// Runs both service-hours hooks once per process on a fake clock before any host here serves a request. A before-write
    /// hook's 250 ms budget is wall-clock and includes the first run's JIT, which a loaded test machine (ring0 runs every
    /// module at once) can exceed — #519; once warm the hooks take a few milliseconds. The product's budget is unchanged.
    /// </summary>
    private static readonly Lazy<Task> _warmUp = new(WarmUpAsync);

    private readonly SqliteConnection _database;
    private readonly WebApplication _app;

    private HttpWorld(SqliteConnection database, WebApplication app)
    {
        _database = database;
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<HttpWorld> StartAsync(CancellationToken cancellationToken)
    {
        await _warmUp.Value;
        var database = new SqliteConnection($"Data Source=field-service-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await database.OpenAsync(cancellationToken);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<AlvoEventOptions>(options => options.DispatchMode = AlvoEventDispatchMode.Manual);
        builder.Services.Configure<AlvoSecretOptions>(options => options.Values["technician_app"] = SigningSecret);
        builder.Services.Configure<AlvoAuthOptions>(AddKeys);
        builder.Services.AddAlvoProblemDetails();
        builder.Services.AddAlvo(alvo => FieldServiceWorld.Compose(alvo.UseSqlite(database.ConnectionString), new RecordingNotifier()));

        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAlvoDataApi();
        app.MapFieldService();
        await app.StartAsync(cancellationToken);
        return new HttpWorld(database, app);
    }

    /// <summary>Best effort: a warm-up that itself fails (it runs cold) is swallowed, so it never fails a test, and is not cached.</summary>
    private static async Task WarmUpAsync()
    {
        try
        {
            await using var alvo = await FieldServiceWorld.StartAsync(CancellationToken.None);
            var north = await alvo.SeedAsync(FieldServiceWorld.North, CancellationToken.None);
            var job = await alvo.CreateJobAsync(north, "WO-0001", CancellationToken.None, scheduledFor: FieldServiceWorld.Now);
            await alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant)).CreateAsync(
                Visit.Create(job.Id, FieldServiceWorld.Technician, FieldServiceWorld.Now, FieldServiceWorld.Now.AddHours(1), tenantId: north.Tenant.Value),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TestContext.Current.SendDiagnosticMessage($"Hook warm-up failed, tests run cold: {exception.Message}");
        }
    }

    public async Task<HttpResponseMessage> SendAsync(
        string path, string key, HttpMethod? method = null, object? body = null, string? idempotency = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-Alvo-Api-Key", $"{key}.{KeySecret}");
        if (idempotency is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotency);
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A request whose JSON body is sent exactly as written — for a body a typed object cannot express, such as a missing member.</summary>
    public async Task<HttpResponseMessage> SendRawAsync(string path, string key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Alvo-Api-Key", $"{key}.{KeySecret}");
        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async Task<JsonObject> ReadAsync(string path, string key)
    {
        using var response = await SendAsync(path, key, HttpMethod.Get);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
    }

    public async Task<JsonObject> CreateAsync(string entity, string key, JsonObject body)
    {
        using var response = await SendAsync($"/api/{entity}", key, HttpMethod.Post, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>A region (as the administrator) and a customer open 07–19 (as the dispatcher), in tenant north.</summary>
    public async Task<(Guid Region, Guid Customer)> SeedAsync()
    {
        var region = await CreateAsync("regions", "admin", new JsonObject { ["code"] = "NORTH", ["name"] = "North" });
        var customer = await CreateAsync("customers", "dispatcher", new JsonObject
        {
            ["tenant_id"] = FieldServiceWorld.North.Value,
            ["name"] = "Kaviareň Dunaj",
            ["tier"] = "standard",
            ["service_opens_hour"] = 7,
            ["service_closes_hour"] = 19,
        });

        return (region["id"]!.GetValue<Guid>(), customer["id"]!.GetValue<Guid>());
    }

    /// <summary>A work order created by the dispatcher in tenant north.</summary>
    public async Task<Guid> NewJobAsync((Guid Region, Guid Customer) seed, string reference, string status = "new", Guid? assignedTo = null)
    {
        var created = await CreateAsync("work_orders", "dispatcher", new JsonObject
        {
            ["tenant_id"] = FieldServiceWorld.North.Value,
            ["reference"] = reference,
            ["title"] = $"Job {reference}",
            ["status"] = status,
            ["priority"] = 3,
            ["access_code"] = "4711",
            ["customer_id"] = seed.Customer,
            ["region_id"] = seed.Region,
            ["assigned_to"] = assignedTo,
        });

        return created["id"]!.GetValue<Guid>();
    }

    /// <summary>The dispatcher dispatches <paramref name="job"/> to the technician at <paramref name="startsAt"/>, for 90 minutes.</summary>
    public async Task DispatchAsync(Guid job, DateTimeOffset startsAt)
    {
        using var dispatched = await SendAsync(
            $"/commands/work-orders/{job}/dispatch",
            "dispatcher",
            body: new { technicianId = FieldServiceWorld.Technician, startsAt, durationMinutes = 90 });
        dispatched.EnsureSuccessStatusCode();
    }

    /// <summary>A Standard Webhooks delivery, signed the way a sender signs it.</summary>
    public async Task<HttpResponseMessage> SignedAsync(string path, string id, string body, string secret = SigningSecret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", $"v1,{signature}");
        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    private static void AddKeys(AlvoAuthOptions options)
    {
        options.DevKeys.Add(Key("dispatcher", Guid.NewGuid(), FieldServiceRoles.Dispatcher));
        options.DevKeys.Add(Key("technician", FieldServiceWorld.Technician, FieldServiceRoles.Technician));
        options.DevKeys.Add(Key("admin", Guid.NewGuid(), FieldServiceRoles.Admin));
        options.DevKeys.Add(Key("dispatcher-south", Guid.NewGuid(), FieldServiceRoles.Dispatcher, FieldServiceWorld.South));
    }

    private static AlvoDevApiKey Key(string id, Guid user, string role, TenantId? tenant = null)
    {
        var key = new AlvoDevApiKey { KeyId = id, Secret = KeySecret, User = user, Tenant = (tenant ?? FieldServiceWorld.North).Value };
        key.Roles.Add(FieldServiceRoles.Authenticated);
        key.Roles.Add(role);
        key.Scopes.Add("*:read");
        key.Scopes.Add("*:write");
        return key;
    }
}
