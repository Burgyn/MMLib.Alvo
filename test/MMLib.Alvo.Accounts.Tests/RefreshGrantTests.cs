using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The refresh grant over HTTP (RFC 6749 §6, spec §5.5, D6): rotation, the grace window that answers the same child
/// (S2 Review Focus 1 and 5), reuse after it ending the whole session, the idle and absolute limits, and one answer for
/// every dead refresh token.
/// </summary>
public sealed class RefreshGrantTests
{
    private const string NeedsTheBearerChannel = "Task 9: the person resolver turns an access token into a Data API caller.";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refresh_rotates_and_the_child_refreshes_again()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, parent) = await world.SignInAsync("sub-1");

        var child = await GrantedAsync(await world.RefreshAsync(parent));
        var grandchild = await GrantedAsync(await world.RefreshAsync(child["refresh_token"]!.GetValue<string>()));

        child.Select(member => member.Key).ShouldBe(["access_token", "token_type", "expires_in", "refresh_token"], ignoreOrder: true);
        child["refresh_token"]!.GetValue<string>().ShouldStartWith(RefreshTokenFormat.Prefix);
        child["refresh_token"]!.GetValue<string>().ShouldNotBe(parent);
        grandchild["refresh_token"]!.GetValue<string>().ShouldNotBe(child["refresh_token"]!.GetValue<string>());
        SessionOf(child["access_token"]!.GetValue<string>()).ShouldBe(SessionOf(access));
        SessionOf(grandchild["access_token"]!.GetValue<string>()).ShouldBe(SessionOf(access));
    }

    [Fact]
    public async Task Two_concurrent_refreshes_get_the_same_child_and_the_session_survives()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var (_, parent) = await world.SignInAsync($"tab-{iteration}");

            var (first, second) = await RefreshTwiceAtOnceAsync(world, parent);

            first["refresh_token"]!.GetValue<string>().ShouldBe(second["refresh_token"]!.GetValue<string>(), $"iteration {iteration}");
            first["access_token"]!.GetValue<string>().ShouldNotBe(second["access_token"]!.GetValue<string>());
            await GrantedAsync(await world.RefreshAsync(first["refresh_token"]!.GetValue<string>()));
        }
    }

    [Fact(Skip = NeedsTheBearerChannel)]
    public async Task Both_access_tokens_of_two_concurrent_refreshes_reach_the_data_api()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (_, parent) = await world.SignInAsync("sub-1");

        var (first, second) = await RefreshTwiceAtOnceAsync(world, parent);

        foreach (var answer in new[] { first, second })
        {
            using var read = await DataApiAsync(world, answer["access_token"]!.GetValue<string>());
            read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.ReadTextAsync());
        }
    }

    [Fact]
    public async Task A_reuse_after_the_window_kills_every_token_of_the_session_before_answering()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (_, parent) = await world.SignInAsync("sub-1");
        var child = (await GrantedAsync(await world.RefreshAsync(parent)))["refresh_token"]!.GetValue<string>();

        world.Clock.Advance(TimeSpan.FromSeconds(11));
        using var reused = await world.RefreshAsync(parent);
        using var childAfter = await world.RefreshAsync(child);

        await InvalidGrantAsync(reused);
        await InvalidGrantAsync(childAfter);
    }

    [Fact(Skip = NeedsTheBearerChannel)]
    public async Task A_reuse_after_the_window_kills_the_access_token_minted_last()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (_, parent) = await world.SignInAsync("sub-1");
        var last = (await GrantedAsync(await world.RefreshAsync(parent)))["access_token"]!.GetValue<string>();

        world.Clock.Advance(TimeSpan.FromSeconds(11));
        await InvalidGrantAsync(await world.RefreshAsync(parent));
        using var read = await DataApiAsync(world, last);

        read.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_reuse_at_exactly_ten_seconds_answers_the_same_child()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (_, parent) = await world.SignInAsync("sub-1");
        var child = (await GrantedAsync(await world.RefreshAsync(parent)))["refresh_token"]!.GetValue<string>();

        world.Clock.Advance(TimeSpan.FromSeconds(10));
        var replayed = await GrantedAsync(await world.RefreshAsync(parent));

        replayed["refresh_token"]!.GetValue<string>().ShouldBe(child);
        await GrantedAsync(await world.RefreshAsync(child));
    }

    [Fact]
    public async Task A_zero_grace_window_revokes_on_any_reuse()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, options => options.RefreshReuseGraceSeconds = 0);
        var (_, parent) = await world.SignInAsync("sub-1");
        var child = (await GrantedAsync(await world.RefreshAsync(parent)))["refresh_token"]!.GetValue<string>();

        await InvalidGrantAsync(await world.RefreshAsync(parent));
        await InvalidGrantAsync(await world.RefreshAsync(child));
    }

    [Fact]
    public async Task An_idle_refresh_token_and_an_expired_session_are_invalid_grant()
    {
        await using var idle = await AccountsWorld.StartAsync(_engine);
        var (_, untouched) = await idle.SignInAsync("idle");
        idle.Clock.Advance(TimeSpan.FromDays(30));
        await InvalidGrantAsync(await idle.RefreshAsync(untouched));

        await using var busy = await AccountsWorld.StartAsync(_engine);
        var (_, refresh) = await busy.SignInAsync("busy");
        for (var day = 20; day < 180; day += 20)
        {
            busy.Clock.Advance(TimeSpan.FromDays(20));
            refresh = (await GrantedAsync(await busy.RefreshAsync(refresh)))["refresh_token"]!.GetValue<string>();
        }

        busy.Clock.Advance(TimeSpan.FromDays(20));
        await InvalidGrantAsync(await busy.RefreshAsync(refresh));
    }

    [Fact]
    public async Task A_session_whose_provider_the_descriptor_dropped_is_invalid_grant()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (_, refresh) = await world.SignInAsync("sub-1");

        await world.ReapplyAsync(descriptor => descriptor.Remove("auth"));

        await InvalidGrantAsync(await world.RefreshAsync(refresh));
    }

    [Fact]
    public async Task A_refresh_without_a_refresh_token_is_invalid_request()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        using var response = await world.TokenAsync(("grant_type", "refresh_token"));

        await TokenExchangeTests.RefusalAsync(response, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_request");
    }

    [Theory]
    [InlineData("an unknown id under a valid MAC")]
    [InlineData("a bad MAC")]
    [InlineData("a session revoked by logout")]
    [InlineData("a token reused after the window")]
    [InlineData("an idle token")]
    [InlineData("a disabled account")]
    public async Task Every_dead_refresh_token_answers_byte_identically(string death)
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, refresh) = await world.SignInAsync("sub-1");
        var key = await world.Api.Services.GetRequiredService<AccountKeys>().RefreshMacKeyAsync(Ct);
        using var reference = await world.RefreshAsync(RefreshTokenFormat.Format(Guid.NewGuid(), key));

        var dead = await KillAsync(world, death, access, refresh);
        using var response = await world.RefreshAsync(dead);

        await InvalidGrantAsync(reference);
        await InvalidGrantAsync(response);
        (await WithoutTraceIdAsync(response)).ShouldBe(await WithoutTraceIdAsync(reference));
    }

    private static async Task<string> KillAsync(AccountsWorld world, string death, string access, string refresh)
    {
        var now = world.Clock.GetUtcNow();
        switch (death)
        {
            case "an unknown id under a valid MAC":
                var key = await world.Api.Services.GetRequiredService<AccountKeys>().RefreshMacKeyAsync(Ct);
                return RefreshTokenFormat.Format(Guid.NewGuid(), key);
            case "a bad MAC":
                return refresh[..^2] + (refresh[^2] == 'A' ? 'B' : 'A') + refresh[^1];
            case "a session revoked by logout":
                await world.Store.RevokeSessionAsync(SessionOf(access), SessionRevocations.Logout, now, Ct);
                return refresh;
            case "a token reused after the window":
                await GrantedAsync(await world.RefreshAsync(refresh));
                world.Clock.Advance(TimeSpan.FromSeconds(11));
                return refresh;
            case "an idle token":
                world.Clock.Advance(TimeSpan.FromDays(30));
                return refresh;
            default:
                await world.Store.SetDisabledAsync(new UserId(Guid.Parse(new JsonWebToken(access).Subject)), true, now, Ct);
                return refresh;
        }
    }

    private static async Task<(JsonObject First, JsonObject Second)> RefreshTwiceAtOnceAsync(AccountsWorld world, string parent)
    {
        var answers = await Task.WhenAll(world.RefreshAsync(parent), world.RefreshAsync(parent));
        using var first = answers[0];
        using var second = answers[1];
        return (await GrantedAsync(first), await GrantedAsync(second));
    }

    private static async Task<JsonObject> GrantedAsync(HttpResponseMessage response)
    {
        var text = await response.ReadTextAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = await response.ReadJsonObjectAsync();
        body.ContainsKey("issued_token_type").ShouldBeFalse("a refresh answers RFC 6749 §5.1, without RFC 8693's member");
        return body;
    }

    private static async Task InvalidGrantAsync(HttpResponseMessage response)
    {
        var problem = await TokenExchangeTests.RefusalAsync(response, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_grant");
        problem["error_description"]!.GetValue<string>().ShouldBe(TokenProblems.RefreshTokenDescription);
    }

    private static async Task<string> WithoutTraceIdAsync(HttpResponseMessage response)
    {
        var body = await response.ReadJsonObjectAsync();
        body.Remove("traceId");
        return $"{(int)response.StatusCode} {response.Content.Headers.ContentType} {body.ToJsonString()}";
    }

    private static Guid SessionOf(string access) => Guid.ParseExact(new JsonWebToken(access).GetPayloadValue<string>("sid"), "N");

    private static Task<HttpResponseMessage> DataApiAsync(AccountsWorld world, string access)
        => world.Api.SendAsync(
            HttpMethod.Get, "/api/time_entries", headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {access}")]);
}
