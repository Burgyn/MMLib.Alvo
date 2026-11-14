using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The signed-in person's own routes (spec §5.5, S2 D8, D16): <c>GET</c> and <c>PATCH /user</c>, <c>POST /logout</c> —
/// reached only with a Bearer token, and never cached.
/// </summary>
public sealed class UserEndpointsTests
{
    private const string User = "/auth/v1/user";

    private const string Logout = "/auth/v1/logout";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task User_answers_the_account_its_providers_and_no_memberships()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");

        using var response = await world.AsPersonAsync(HttpMethod.Get, User, access);

        var body = await OkAsync(response);
        body.Select(member => member.Key).ShouldBe(
            ["id", "email", "emailTrusted", "name", "avatarUrl", "providers", "memberships"], ignoreOrder: true);
        body["id"]!.GetValue<Guid>().ShouldNotBe(Guid.Empty);
        body["email"]!.GetValue<string>().ShouldBe("ada@gmail.com");
        body["emailTrusted"]!.GetValue<bool>().ShouldBeTrue();
        body["providers"]!.AsArray().Select(provider => provider!.GetValue<string>()).ShouldBe(["google"]);
        body["memberships"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Patching_the_name_changes_only_the_name_and_survives_the_next_sign_in()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");
        using var before = await world.AsPersonAsync(HttpMethod.Get, User, access);
        var original = await OkAsync(before);

        using var patched = await world.AsPersonAsync(HttpMethod.Patch, User, access, new JsonObject { ["name"] = "  Ada L.  " });
        var (again, _) = await world.SignInAsync("sub-1", email: "ada.lovelace@gmail.com");
        using var after = await world.AsPersonAsync(HttpMethod.Get, User, again);

        var answered = await OkAsync(patched);
        answered["name"]!.GetValue<string>().ShouldBe("Ada L.");
        answered.Where(member => member.Key != "name").Select(member => member.Value?.ToJsonString())
            .ShouldBe(original.Where(member => member.Key != "name").Select(member => member.Value?.ToJsonString()));
        var current = await OkAsync(after);
        current["name"]!.GetValue<string>().ShouldBe("Ada L.");
        current["email"]!.GetValue<string>().ShouldBe("ada.lovelace@gmail.com");
    }

    [Theory]
    [InlineData("")]
    [InlineData("101 characters")]
    [InlineData("a\u0007b")]
    public async Task A_bad_name_is_a_validation_problem(string name)
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");

        using var response = await world.AsPersonAsync(
            HttpMethod.Patch, User, access, new JsonObject { ["name"] = name == "101 characters" ? new string('a', 101) : name });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var problem = await response.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.BaseUri + AlvoProblemTypes.Validation);
        problem["violations"]!.AsArray().Select(violation => violation!["pointer"]!.GetValue<string>()).ShouldBe(["/name"]);
    }

    [Theory]
    [InlineData("{\"name\":\"a\\ud800\"}", "/name", "invalid-value")]
    [InlineData("{\"name\":\"\\udc00b\"}", "/name", "invalid-value")]
    [InlineData("{\"name\":\"Ada\",\"\\ud800\":1}", "", "malformed-json")]
    public async Task Half_a_surrogate_pair_is_a_validation_problem_not_a_500(string json, string at, string code)
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");

        using var response = await world.Api.SendRawAsync(
            HttpMethod.Patch,
            User,
            content: new StringContent(json, Encoding.UTF8, "application/json"),
            headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {access}")]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        var violation = (await response.ReadJsonObjectAsync())["violations"]!.AsArray().ShouldHaveSingleItem()!;
        violation["pointer"]!.GetValue<string>().ShouldBe(at);
        violation["code"]!.GetValue<string>().ShouldBe(code);
    }

    /// <summary>
    /// A body nested to the bound is answered about its members; one level deeper — even when the innermost container is
    /// empty — is the named depth refusal, never a 500 from the parse.
    /// </summary>
    [Theory]
    [InlineData(31, false)]
    [InlineData(32, true)]
    public async Task An_empty_innermost_container_past_the_depth_bound_is_refused_by_name(int arrays, bool tooDeep)
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");
        var json = "{\"name\":\"Ada\",\"x\":" + new string('[', arrays) + new string(']', arrays) + "}";

        using var response = await world.Api.SendRawAsync(
            HttpMethod.Patch,
            User,
            content: new StringContent(json, Encoding.UTF8, "application/json"),
            headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {access}")]);

        var text = await response.ReadTextAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, text);
        text.Contains("nests deeper than 32", StringComparison.Ordinal).ShouldBe(tooDeep, text);
    }

    [Fact]
    public async Task A_patch_that_is_not_json_is_unsupported()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");

        using var response = await world.Api.SendRawAsync(
            HttpMethod.Patch,
            User,
            content: new StringContent("name=Ada", Encoding.UTF8, "application/x-www-form-urlencoded"),
            headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {access}")]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, await response.ReadTextAsync());
        (await response.ReadJsonObjectAsync())["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.BaseUri + AlvoProblemTypes.UnsupportedMediaType);
    }

    [Fact]
    public async Task Logout_is_idempotent_and_kills_the_refresh_token()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, refresh) = await world.SignInAsync("sub-1");

        using var first = await world.AsPersonAsync(HttpMethod.Post, Logout, access);
        using var second = await world.AsPersonAsync(HttpMethod.Post, Logout, access);
        using var refreshed = await world.RefreshAsync(refresh);
        using var user = await world.AsPersonAsync(HttpMethod.Get, User, access);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        first.Headers.CacheControl!.NoStore.ShouldBeTrue();
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await TokenExchangeTests.RefusalAsync(refreshed, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_grant");
        await PersonResolverTests.InvalidTokenAsync(user);
    }

    [Fact]
    public async Task Logout_with_a_token_that_is_not_ours_is_401()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        using var response = await world.AsPersonAsync(HttpMethod.Post, Logout, world.IdToken("sub-1"));

        await PersonResolverTests.InvalidTokenAsync(response);
    }

    [Theory]
    [InlineData("GET", User)]
    [InlineData("PATCH", User)]
    [InlineData("POST", Logout)]
    public async Task User_without_a_bearer_is_401_with_a_bearer_challenge(string method, string path)
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        using var response = await world.Api.SendAsync(new HttpMethod(method), path);

        await BearerChallengeAsync(response);
    }

    [Fact]
    public async Task An_api_key_is_not_a_person_here()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        using var response = await world.Api.SendAsync(HttpMethod.Get, User, AccountsWorld.Operator);

        await BearerChallengeAsync(response);
    }

    [Fact]
    public async Task An_api_key_beside_a_bearer_is_an_invalid_request()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");

        using var response = await world.Api.SendAsync(
            HttpMethod.Get, User, AccountsWorld.Operator, headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {access}")]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer error=\"invalid_request\"");
    }

    private static async Task<JsonObject> OkAsync(HttpResponseMessage response)
    {
        var body = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        return body;
    }

    private static async Task BearerChallengeAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.ReadTextAsync());
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await response.ReadJsonObjectAsync())["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.BaseUri + AlvoProblemTypes.Unauthenticated);
    }
}
