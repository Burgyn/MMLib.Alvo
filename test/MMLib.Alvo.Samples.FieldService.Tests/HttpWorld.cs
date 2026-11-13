using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
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
        var database = new SqliteConnection($"Data Source=field-service-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await database.OpenAsync(cancellationToken);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<AlvoEventOptions>(options => options.DispatchMode = AlvoEventDispatchMode.Manual);
        builder.Services.Configure<AlvoSecretOptions>(options => options.Values["technician_app"] = SigningSecret);
        builder.Services.Configure<AlvoAuthOptions>(AddKeys);
        builder.Services.AddAlvoProblemDetails();
        builder.Services.AddAlvo(alvo => FieldServiceWorld.Compose(alvo.UseSqlite(database.ConnectionString)));

        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAlvoDataApi();
        app.MapFieldService();
        await app.StartAsync(cancellationToken);
        return new HttpWorld(database, app);
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
    }

    private static AlvoDevApiKey Key(string id, Guid user, string role)
    {
        var key = new AlvoDevApiKey { KeyId = id, Secret = KeySecret, User = user, Tenant = FieldServiceWorld.North.Value };
        key.Roles.Add(FieldServiceRoles.Authenticated);
        key.Roles.Add(role);
        key.Scopes.Add("*:read");
        key.Scopes.Add("*:write");
        return key;
    }
}
