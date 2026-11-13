using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Testing;
using Shouldly;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FirstExtension.Tests;

/// <summary>
/// The page's HTTP promise: the snippet's own <c>appsettings.json</c> key, over the same registration as
/// <c>DeliveryDeskProgram</c>, gets 422 <c>validation</c> with the hook's code for a postcode the courier does not serve.
/// </summary>
public sealed class DeliveryDeskHttpTests : IAsyncLifetime
{
    private const string Secret = "a-dev-secret-at-least-thirty-two-characters";
    private readonly SqliteConnection _database = new($"Data Source=delivery-desk-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Snippets => Path.Combine(RepositoryRoot.Find(), "website", "src", "snippets", "extending", "first-extension");

    public async ValueTask InitializeAsync()
    {
        await _database.OpenAsync(Ct);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddJsonFile(Path.Combine(Snippets, "appsettings.json"));
        builder.Configuration.AddInMemoryCollection([new("Alvo:Auth:DevKeys:0:Secret", Secret)]);
        builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));
        builder.Services.AddSingleton<IDeliveryAreas>(new OnlyBratislava());
        builder.Services.AddAlvo(alvo => alvo
            .UseSqlite(_database.ConnectionString)
            .FromDescriptor(Path.Combine(AppContext.BaseDirectory, "delivery-desk.alvo.json"))
            .AddDataApi(api => api.RoutePrefix = "/api/alvo")
            .AddDeliveryDesk());

        _app = builder.Build();
        _app.MapAlvoDataApi();
        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    [Fact]
    public async Task The_clerk_key_gets_422_with_the_hook_s_code_and_a_covered_postcode_is_created()
    {
        using var refused = await CreateAsync("040 01");
        using var created = await CreateAsync("811 01");

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        problem.RootElement.GetProperty("type").GetString().ShouldEndWith("/validation");
        var violation = problem.RootElement.GetProperty("violations")[0];
        (violation.GetProperty("code").GetString(), violation.GetProperty("pointer").GetString()).ShouldBe(("postcode-not-covered", "/postcode"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task<HttpResponseMessage> CreateAsync(string postcode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/alvo/orders")
        {
            Content = JsonContent.Create(new { customer = "Ada", postcode }),
        };
        request.Headers.Add("X-Alvo-Api-Key", $"clerk.{Secret}");

        return await _client.SendAsync(request, Ct);
    }

    private sealed class OnlyBratislava : IDeliveryAreas
    {
        public Task<bool> CoversAsync(string postcode, CancellationToken cancellationToken) => Task.FromResult(postcode.StartsWith('8'));
    }
}
