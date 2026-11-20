using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using System.Net;
using static MMLib.Alvo.Api.Tests.AppTokenFakes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// S5 plan D8/D9: the Management API refuses a personal or service token — an app credential — with a fixed 403, on every
/// route, however the project's <c>access</c> block reads; an unusable token is the ordinary 401 of any key, and the same
/// token still reads the Data API.
/// </summary>
public sealed class AppTokenManagementTests
{
    private const string Detail =
        "Personal and service tokens are app credentials and never manage a project. Use an operator session or a configured API key.";

    private static readonly Guid _tenant = Guid.NewGuid();
    private static readonly TestApiKey _operator = new("mgmt-operator", ["authenticated"], ["*:write"]);

    [Theory]
    [InlineData(ApiKeyKind.Personal)]
    [InlineData(ApiKeyKind.Service)]
    public async Task A_token_is_refused_by_every_management_route(ApiKeyKind kind)
    {
        var (store, token) = StoreWith(kind);
        await using var world = await WorldAsync(store);
        var addresses = world.ManagementRouteEndpoints().Select(AlvoApiWorld.AddressOf).ToList();

        addresses.ShouldNotBeEmpty();
        foreach (var (method, path) in addresses)
        {
            using var response = await world.SendAsync(method, path, headers: Presenting(world, token));

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
            (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
            (await response.ReadProblemDetailAsync()).ShouldBe(Detail);
        }

        addresses.Select(address => address.Method).ShouldContain(HttpMethod.Get, "a read route");
        addresses.Select(address => address.Method).ShouldContain(method => method != HttpMethod.Get, "a write route");
    }

    [Fact]
    public async Task An_unusable_app_token_is_401_like_any_key()
    {
        var (store, token) = StoreWith(ApiKeyKind.Personal);
        await using var world = await WorldAsync(store);

        using var wrongSecret = await world.SendAsync(
            HttpMethod.Get, "/management/info", headers: [new(world.CredentialHeaderName, token.KeyId + ".not-the-secret")]);
        store.Expire(token.KeyId);
        using var expired = await world.SendAsync(HttpMethod.Get, "/management/info", headers: Presenting(world, token));

        wrongSecret.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_configured_key_still_manages_as_before()
    {
        var (store, _) = StoreWith(ApiKeyKind.Personal);
        await using var world = await WorldAsync(store);

        using var response = await world.SendAsync(HttpMethod.Get, "/management/info", _operator);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_bearer_beside_an_app_token_on_management_is_judged_by_the_token()
    {
        var (store, token) = StoreWith(ApiKeyKind.Service);
        await using var world = await WorldAsync(store);

        using var response = await world.SendAsync(
            HttpMethod.Get, "/management/info", headers: [.. Presenting(world, token), new("Authorization", "Bearer person-token")]);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Management ignores Authorization (spec 5.7)");
        (await response.ReadProblemDetailAsync()).ShouldBe(Detail);
    }

    [Theory]
    [InlineData(ApiKeyKind.Personal)]
    [InlineData(ApiKeyKind.Service)]
    public async Task The_same_token_reads_the_data_api(ApiKeyKind kind)
    {
        var (store, token) = StoreWith(kind);
        await using var world = await WorldAsync(store);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/regions", headers: Presenting(world, token));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the refusal is Management's only");
    }

    private static Task<AlvoApiWorld> WorldAsync(FakeAppTokenStore store) =>
        AlvoApiWorld.FromDescriptorAsync(
            "token-desk.alvo.json",
            [_operator],
            new AlvoApiWorldSetup(
                ConfigureServices: services => services.AddSingleton<IAppTokenStore>(store),
                MapManagementApi: true));

    private static (FakeAppTokenStore Store, MintedToken Token) StoreWith(ApiKeyKind kind) =>
        AppTokenFakes.StoreWith(kind, _tenant, "regions:read");
}
