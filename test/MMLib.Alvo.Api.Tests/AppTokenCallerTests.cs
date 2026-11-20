using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using System.Net;
using System.Text.Json.Nodes;
using static MMLib.Alvo.Api.Tests.AppTokenFakes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// S5 final review I1: a host's own <c>.WithAlvoCaller()</c> endpoint names no entity a token's scopes could be checked
/// against, so it refuses a personal or service token with a fixed 403 — while a configured key, a Bearer person and the
/// anonymous caller reach it as before, and a mapped command, which does name its entity, admits a token whose scopes
/// cover it and refuses one whose scopes do not.
/// </summary>
public sealed class AppTokenCallerTests
{
    private const string Detail =
        "Personal and service tokens are not accepted on custom endpoints yet: a custom endpoint names no entity a token's "
        + "scopes could be checked against. Use the generated Data API or a command, or call this endpoint with a signed-in "
        + "session or a configured API key.";

    private const string PersonToken = "tc-person";

    private static readonly Guid _tenant = Guid.NewGuid();
    private static readonly TestApiKey _seeder = new("tc-seeder", ["dispatcher", "authenticated"], ["*:read", "*:write"]);
    private static readonly TestApiKey _clerk = new("tc-clerk", ["authenticated"], ["regions:read"]);

    [Theory]
    [InlineData(ApiKeyKind.Personal)]
    [InlineData(ApiKeyKind.Service)]
    public async Task A_token_on_a_custom_endpoint_is_refused_with_the_fixed_403_before_the_handler(ApiKeyKind kind)
    {
        var (store, token) = StoreWith(kind, _tenant, "regions:read", "regions:write");
        var calls = new Calls();
        await using var world = await WorldAsync(store, calls);

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", headers: Presenting(world, token));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "even a token covering every entity: the endpoint names none");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        (await response.ReadProblemDetailAsync()).ShouldBe(Detail);
        calls.Count.ShouldBe(0, "a refused token never reaches the handler");
    }

    [Fact]
    public async Task An_unusable_token_on_a_custom_endpoint_is_401_like_any_key()
    {
        var (store, token) = StoreWith(ApiKeyKind.Personal, _tenant, "regions:read");
        await using var world = await WorldAsync(store, new Calls());

        using var response = await world.SendAsync(
            HttpMethod.Get, "/whoami", headers: [new(world.CredentialHeaderName, token.KeyId + ".not-the-secret")]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "resolution comes first, as on Management");
    }

    [Fact]
    public async Task A_configured_key_a_bearer_person_and_the_anonymous_caller_reach_a_custom_endpoint_as_before()
    {
        var calls = new Calls();
        await using var world = await WorldAsync(new FakeAppTokenStore(), calls);

        using var key = await world.SendAsync(HttpMethod.Get, "/whoami", _clerk);
        using var person = await world.SendAsync(
            HttpMethod.Get, "/whoami", headers: [new("Authorization", $"Bearer {PersonToken}")]);
        using var anonymous = await world.SendAsync(HttpMethod.Get, "/whoami");

        key.StatusCode.ShouldBe(HttpStatusCode.OK);
        person.StatusCode.ShouldBe(HttpStatusCode.OK);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.OK);
        calls.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(ApiKeyKind.Personal)]
    [InlineData(ApiKeyKind.Service)]
    public async Task A_command_admits_a_token_whose_scopes_cover_its_entity(ApiKeyKind kind)
    {
        var (store, token) = StoreWith(kind, _tenant, "regions:read", "regions:write");
        var calls = new Calls();
        await using var world = await WorldAsync(store, calls);
        var region = await RegionAsync(world);

        using var response = await world.SendAsync(
            HttpMethod.Post, $"/commands/regions/{region}/touch", headers: Presenting(world, token));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        calls.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(ApiKeyKind.Personal)]
    [InlineData(ApiKeyKind.Service)]
    public async Task A_command_refuses_a_token_outside_its_scopes_403_out_of_scope(ApiKeyKind kind)
    {
        var (store, token) = StoreWith(kind, _tenant, "regions:read");
        var calls = new Calls();
        await using var world = await WorldAsync(store, calls);
        var region = await RegionAsync(world);

        using var bound = await world.SendAsync(
            HttpMethod.Post, $"/commands/regions/{region}/touch", headers: Presenting(world, token));
        using var unbound = await world.SendAsync(HttpMethod.Post, "/commands/ping", headers: Presenting(world, token));

        bound.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "regions:read does not write regions");
        (await bound.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.OutOfScope);
        unbound.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an unbound command needs *:write, which no token can hold");
        (await unbound.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.OutOfScope);
        calls.Count.ShouldBe(0);
    }

    private static async Task<Guid> RegionAsync(AlvoApiWorld world)
    {
        using var created = await world.SendAsync(HttpMethod.Post, "/api/regions", _seeder, body: new JsonObject { ["code"] = "N" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static Task<AlvoApiWorld> WorldAsync(FakeAppTokenStore store, Calls calls)
    {
        var people = new TestPersonResolver((PersonToken, TestPersonResolver.Person("person:tc", null)));
        return AlvoApiWorld.FromDescriptorAsync(
            "token-caller.alvo.json",
            [_seeder, _clerk],
            new AlvoApiWorldSetup(
                MapAlvoProblemDetails: true,
                ConfigureServices: services =>
                {
                    services.AddSingleton<IAppTokenStore>(store);
                    people.Register(services);
                },
                ConfigureApp: app => Map(app, calls)));
    }

    private static void Map(WebApplication app, Calls calls)
    {
        app.MapGet("/whoami", (AlvoCaller caller) => calls.Answer(caller.Context.User.Value)).WithAlvoCaller();
        app.MapAlvoCommand("touch_region", "/commands/regions/{id:guid}/touch", "regions", "'authenticated' in @user.roles", () => calls.Answer(Guid.Empty));
        app.MapAlvoCommand("ping", "/commands/ping", "'authenticated' in @user.roles", () => calls.Answer(Guid.Empty));
    }

    /// <summary>How many times a handler ran.</summary>
    private sealed class Calls
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        internal IResult Answer(Guid user)
        {
            Interlocked.Increment(ref _count);
            return Results.Ok(new { user });
        }
    }
}
