using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;
using System.Net;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The token endpoint's budget and the auth routes' CORS (spec §7.3, S2 D3): charged and applied by Alvo on its own
/// routes, with no pipeline step in the host.
/// </summary>
/// <remarks>
/// The test server presents no client address, so every request here is charged to the one shared partition — which is
/// exactly the "one client" these facts need.
/// </remarks>
public sealed class TokenRateLimitTests
{
    private const string Allowed = "https://app.example.test";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task The_token_endpoint_answers_429_after_its_limit()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, api: api => api.RateLimits.Token = 2);

        using var first = await world.RefreshAsync("not-a-token");
        using var second = await world.RefreshAsync("not-a-token");
        using var third = await world.RefreshAsync("not-a-token");

        first.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, await third.ReadTextAsync());
        (await third.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.RateLimited);
        var retryAfter = int.Parse(third.Headers.GetValues("Retry-After").ShouldHaveSingleItem(), System.Globalization.CultureInfo.InvariantCulture);
        retryAfter.ShouldBeInRange(1, 60);
        third.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task The_limit_does_not_touch_the_data_api()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, api: api => api.RateLimits.Token = 1);
        var (access, _) = await world.SignInAsync("sub-1");

        for (var i = 0; i < 3; i++)
        {
            using var response = await world.AsPersonAsync(HttpMethod.Get, "/api/time_entries", access);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        }

        using var token = await world.RefreshAsync("not-a-token");
        token.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task An_auth_route_from_an_allowed_origin_carries_cors_headers_and_answers_its_preflight()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, api: api => api.Cors.Origins.Add(Allowed));

        using var token = await world.Api.SendRawAsync(
            HttpMethod.Post,
            AccountsWorld.TokenPath,
            content: new FormUrlEncodedContent([new("grant_type", "refresh_token"), new("refresh_token", "not-a-token")]),
            headers: [new("Origin", Allowed)]);
        using var preflight = await world.Api.SendAsync(
            HttpMethod.Options,
            AccountsWorld.TokenPath,
            headers: [new("Origin", Allowed), new("Access-Control-Request-Method", "POST")]);
        using var inserted = await world.Api.SendAsync(
            HttpMethod.Get, "/.well-known/oauth-authorization-server/auth/v1", headers: [new("Origin", Allowed)]);

        token.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        token.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
        preflight.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        preflight.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
        preflight.Headers.GetValues("Access-Control-Allow-Methods").ShouldHaveSingleItem().ShouldContain("POST");
        inserted.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
    }
}
