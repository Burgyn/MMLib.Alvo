using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Accounts;

/// <summary>
/// Level 1 of spec §3 end to end, the same facts on SQLite and on PostgreSQL (spec §14 AC1, AC3, AC7; S2 Review Focus 1):
/// a person exchanges a Google ID token, writes and reads their own rows with the access token, refreshes, signs out — and
/// every token of the session is dead; reuse inside the grace window answers the same child, after it kills the session;
/// two people never see each other's rows; an API key beside sign-in works exactly as before; a disabled person is
/// refused on the next request.
/// </summary>
/// <remarks>
/// Each engine contributes only <see cref="Engine"/>: a fact written in one leg is a fact the other never has to satisfy.
/// </remarks>
public abstract class AccountsEngineFacts
{
    private const string Entries = "/api/time_entries";

    private const string Logout = "/auth/v1/logout";

    private static readonly TestApiKey _developer = new("accounts-developer", ["authenticated"], ["*:read", "*:write"]);

    /// <summary>Gets the engine every world in this suite runs on.</summary>
    protected abstract AlvoApiEngine Engine { get; }

    [Fact]
    public async Task Level_one_end_to_end()
    {
        await using var world = await AccountsWorld.StartAsync(Engine);
        var (access, refresh) = await world.SignInAsync("ada");

        using var created = await world.AsPersonAsync(HttpMethod.Post, Entries, access, Entry(30));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await created.ReadJsonObjectAsync())["created_by"]!.GetValue<string>().ShouldBe(new JsonWebToken(access).Subject);
        (await ItemsAsync(world, access)).ShouldBe(1);

        var (renewed, child) = await RefreshedAsync(world, refresh);
        (await ItemsAsync(world, renewed)).ShouldBe(1, "the refreshed access token is the same person");

        using var logout = await world.AsPersonAsync(HttpMethod.Post, Logout, renewed);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await InvalidTokenAsync(await world.AsPersonAsync(HttpMethod.Get, Entries, renewed));
        await InvalidGrantAsync(await world.RefreshAsync(child));
        await InvalidGrantAsync(await world.RefreshAsync(refresh));
    }

    [Fact]
    public async Task Reuse_inside_the_window_returns_the_same_child_and_after_it_kills_the_session()
    {
        await using var world = await AccountsWorld.StartAsync(Engine);
        var (_, parent) = await world.SignInAsync("ada");
        var (_, child) = await RefreshedAsync(world, parent);

        world.Clock.Advance(TimeSpan.FromSeconds(10));
        var (replayedAccess, replayed) = await RefreshedAsync(world, parent);
        replayed.ShouldBe(child, "inside the window the parent answers the child it already made");
        (await ItemsAsync(world, replayedAccess)).ShouldBe(0);

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        await InvalidGrantAsync(await world.RefreshAsync(parent));
        await InvalidGrantAsync(await world.RefreshAsync(child));
        await InvalidTokenAsync(await world.AsPersonAsync(HttpMethod.Get, Entries, replayedAccess));
    }

    [Fact]
    public async Task Two_concurrent_refreshes_get_the_same_child_and_the_session_survives()
    {
        await using var world = await AccountsWorld.StartAsync(Engine);
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var (_, parent) = await world.SignInAsync($"tab-{iteration}");

            var answers = await Task.WhenAll(world.RefreshAsync(parent), world.RefreshAsync(parent));
            var first = await GrantedAsync(answers[0]);
            var second = await GrantedAsync(answers[1]);

            second.Refresh.ShouldBe(first.Refresh, $"iteration {iteration}");
            (await ItemsAsync(world, first.Access)).ShouldBe(0);
            (await ItemsAsync(world, second.Access)).ShouldBe(0);
            await RefreshedAsync(world, first.Refresh);
        }
    }

    [Fact]
    public async Task Two_people_never_see_each_other_s_rows()
    {
        await using var world = await AccountsWorld.StartAsync(Engine);
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var adas = await CreatedIdAsync(world, ada, 30);
        await CreatedIdAsync(world, bob, 45);

        (await ItemsAsync(world, ada)).ShouldBe(1);
        (await ItemsAsync(world, bob)).ShouldBe(1);
        using var read = await world.AsPersonAsync(HttpMethod.Get, $"{Entries}/{adas}", bob);
        using var changed = await world.AsPersonAsync(HttpMethod.Patch, $"{Entries}/{adas}", bob, Entry(1));
        using var deleted = await world.AsPersonAsync(HttpMethod.Delete, $"{Entries}/{adas}", bob);

        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        changed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var still = await world.AsPersonAsync(HttpMethod.Get, $"{Entries}/{adas}", ada);
        (await still.ReadJsonObjectAsync())["minutes"]!.GetValue<int>().ShouldBe(30);
    }

    [Fact]
    public async Task An_api_key_beside_sign_in_behaves_exactly_as_before()
    {
        await using var world = await AccountsWorld.StartAsync(Engine, keys: [_developer]);
        var (access, _) = await world.SignInAsync("ada");
        await CreatedIdAsync(world, access, 30);
        var basic = new KeyValuePair<string, string>("Authorization", "Basic aG9zdDpvd24=");

        using var created = await world.Api.SendAsync(HttpMethod.Post, Entries, _developer, body: Entry(15));
        using var listed = await world.Api.SendAsync(HttpMethod.Get, Entries, _developer, headers: [basic]);
        using var both = await world.Api.SendAsync(
            HttpMethod.Get, Entries, _developer, headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {access}")]);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await created.ReadJsonObjectAsync())["created_by"]!.GetValue<Guid>().ShouldBe(_developer.User);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.ReadTextAsync());
        (await listed.ReadJsonObjectAsync())["items"]!.AsArray().Count.ShouldBe(1, "the key's own row; another scheme is the host's");
        both.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await both.ReadTextAsync());
        both.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
        (await both.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Unauthenticated);
    }

    [Fact]
    public async Task A_disabled_person_is_refused_on_the_next_request()
    {
        await using var world = await AccountsWorld.StartAsync(Engine);
        var (access, refresh) = await world.SignInAsync("ada");
        (await ItemsAsync(world, access)).ShouldBe(0);

        using var disabled = await world.Api.SendAsync(
            HttpMethod.Put,
            $"/management/projects/time-tracker/accounts/{new JsonWebToken(access).Subject}/disabled",
            AccountsWorld.Operator,
            body: new JsonObject { ["disabled"] = true });

        disabled.StatusCode.ShouldBe(HttpStatusCode.NoContent, await disabled.ReadTextAsync());
        await InvalidTokenAsync(await world.AsPersonAsync(HttpMethod.Get, Entries, access));
        await InvalidGrantAsync(await world.RefreshAsync(refresh));
        using var again = await world.ExchangeAsync(world.IdToken("ada"));
        again.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await again.ReadTextAsync());
        (await again.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.SignInRefused);
    }

    private static JsonObject Entry(int minutes) => new() { ["minutes"] = minutes };

    private static async Task<Guid> CreatedIdAsync(AccountsWorld world, string access, int minutes)
    {
        using var created = await world.AsPersonAsync(HttpMethod.Post, Entries, access, Entry(minutes));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static async Task<int> ItemsAsync(AccountsWorld world, string access)
    {
        using var list = await world.AsPersonAsync(HttpMethod.Get, Entries, access);
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.ReadTextAsync());
        return (await list.ReadJsonObjectAsync())["items"]!.AsArray().Count;
    }

    private static async Task<(string Access, string Refresh)> RefreshedAsync(AccountsWorld world, string refresh)
        => await GrantedAsync(await world.RefreshAsync(refresh));

    private static async Task<(string Access, string Refresh)> GrantedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.ReadJsonObjectAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
            return (body["access_token"]!.GetValue<string>(), body["refresh_token"]!.GetValue<string>());
        }
    }

    private static async Task InvalidGrantAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.ReadJsonObjectAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body.ToJsonString());
            body["error"]!.GetValue<string>().ShouldBe("invalid_grant");
        }
    }

    private static async Task InvalidTokenAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.ReadTextAsync());
            response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer error=\"invalid_token\"");
        }
    }
}
