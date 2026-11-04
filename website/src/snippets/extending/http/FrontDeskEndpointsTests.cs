using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Auth;
using Shouldly;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace FrontDesk.Tests;

public sealed class FrontDeskEndpointsTests : IAsyncLifetime
{
    private const string KeySecret = "s3cret-value-long-enough-for-the-floor";
    private static readonly Guid _clerk = Guid.NewGuid();
    private readonly SqliteConnection _database = new($"Data Source=front-desk-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.OpenAsync(Ct);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(limiter => limiter.AddFrontDeskLimits());
        builder.Services.Configure<AlvoAuthOptions>(options =>
        {
            var key = new AlvoDevApiKey { KeyId = "clerk", Secret = KeySecret, User = _clerk };
            key.Roles.Add("clerk");
            key.Scopes.Add("*:read");
            key.Scopes.Add("*:write");
            options.DevKeys.Add(key);
        });
        builder.Services.AddAlvo(alvo => alvo.UseSqlite(_database.ConnectionString).FromDescriptor(Path.Combine(AppContext.BaseDirectory, "front-desk.alvo.json")));

        _app = builder.Build();
        _app.UseRateLimiter();
        _app.MapFrontDesk();
        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    [Fact]
    public async Task No_credential_is_the_anonymous_caller_and_a_key_is_its_own()
    {
        var anonymous = await _client.GetFromJsonAsync<CallerView>("/me", Ct);
        var clerk = await GetAsync<CallerView>("/me", "clerk");

        anonymous!.Actor.ShouldBe("Anonymous");
        clerk!.User.ShouldBe(_clerk);
    }

    [Fact]
    public async Task A_key_that_cannot_be_used_is_401_before_the_handler()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("X-Alvo-Api-Key", "clerk.wrong-secret-that-is-long-enough");

        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_plain_endpoint_reads_as_the_resolved_caller_under_the_descriptor_s_rules()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rooms") { Content = JsonContent.Create(new { number = "12" }) };
        request.Headers.Add("X-Alvo-Api-Key", $"clerk.{KeySecret}");
        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await GetAsync<int>("/me/rooms", "clerk")).ShouldBe(1);
        (await _client.GetFromJsonAsync<int>("/me/rooms", Ct)).ShouldBe(0, "the list rule admits clerks only");
    }

    [Fact]
    public async Task ForEntity_limits_the_rooms_routes_and_no_other()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add(await StatusAsync("/api/rooms"));
        }

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await StatusAsync("/api/guests")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task One_policy_partitioned_on_the_entity_limits_each_entity_on_its_own()
    {
        var guests = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            guests.Add(await StatusAsync("/api/guests"));
        }

        guests.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await StatusAsync("/api/bookings")).ShouldBe(HttpStatusCode.OK, "bookings has a partition of its own under the same policy");
    }

    [Fact]
    public void A_convention_reads_alvo_s_endpoint_metadata()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        endpoints.Count(endpoint => endpoint.Metadata.GetMetadata<AuditedWrite>() is { Entity: "rooms" }).ShouldBe(7, "create, update, replace, delete and the three batch routes");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task<T?> GetAsync<T>(string path, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Alvo-Api-Key", $"{key}.{KeySecret}");
        using var response = await _client.SendAsync(request, Ct);
        return await response.Content.ReadFromJsonAsync<T>(Ct);
    }

    private async Task<HttpStatusCode> StatusAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Alvo-Api-Key", $"clerk.{KeySecret}");
        using var response = await _client.SendAsync(request, Ct);
        return response.StatusCode;
    }
}
