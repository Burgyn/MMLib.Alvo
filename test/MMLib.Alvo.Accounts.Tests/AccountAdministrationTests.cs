using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// An operator disables an end user's account through the Management API (spec §5.6): every session ends in the same
/// transaction, so the next request and the next refresh are refused, and the next sign-in too — until it is enabled,
/// which lets the person sign in again and brings no old session back.
/// </summary>
/// <remarks>
/// Over a descriptor whose <c>access</c> block names an administrator and a developer by role, so the level is the
/// descriptor's decision, not the bootstrap administrator's standing exception.
/// </remarks>
public sealed class AccountAdministrationTests
{
    private const string Descriptor = "time-tracker-operated.alvo.json";

    private const string Entries = "/api/time_entries";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    private static readonly TestApiKey _admin = new("accounts-admin", ["operator"], ["*:read"]);

    private static readonly TestApiKey _developer = new("accounts-developer", ["developer"], ["*:read"]);

    [Fact]
    public async Task Disabling_an_account_ends_its_requests_refreshes_and_sign_ins()
    {
        await using var world = await StartAsync();
        var (access, refresh) = await world.SignInAsync("sub-1");

        using var disabled = await SetDisabledAsync(world, AccountOf(access), disabled: true, _admin);
        using var data = await world.AsPersonAsync(HttpMethod.Get, Entries, access);
        using var refreshed = await world.RefreshAsync(refresh);
        using var exchanged = await world.ExchangeAsync(world.IdToken("sub-1"));

        disabled.StatusCode.ShouldBe(HttpStatusCode.NoContent, await disabled.ReadTextAsync());
        await PersonResolverTests.InvalidTokenAsync(data);
        await TokenExchangeTests.RefusalAsync(refreshed, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_grant");
        await TokenExchangeTests.RefusalAsync(exchanged, HttpStatusCode.Forbidden, AlvoProblemTypes.SignInRefused, "access_denied");
    }

    [Fact]
    public async Task Enabling_lets_them_sign_in_again_and_old_sessions_stay_dead()
    {
        await using var world = await StartAsync();
        var (access, refresh) = await world.SignInAsync("sub-1");
        var account = AccountOf(access);
        (await SetDisabledAsync(world, account, disabled: true, _admin)).Dispose();

        using var enabled = await SetDisabledAsync(world, account, disabled: false, _admin);
        using var oldData = await world.AsPersonAsync(HttpMethod.Get, Entries, access);
        using var oldRefresh = await world.RefreshAsync(refresh);
        var (again, _) = await world.SignInAsync("sub-1");
        using var newData = await world.AsPersonAsync(HttpMethod.Get, Entries, again);

        enabled.StatusCode.ShouldBe(HttpStatusCode.NoContent, await enabled.ReadTextAsync());
        await PersonResolverTests.InvalidTokenAsync(oldData);
        await TokenExchangeTests.RefusalAsync(oldRefresh, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_grant");
        AccountOf(again).ShouldBe(account, "enabling restores the account, not a new one");
        newData.StatusCode.ShouldBe(HttpStatusCode.OK, await newData.ReadTextAsync());
    }

    [Fact]
    public async Task A_developer_cannot_disable_an_account()
    {
        await using var world = await StartAsync();
        var (access, _) = await world.SignInAsync("sub-1");

        using var refused = await SetDisabledAsync(world, AccountOf(access), disabled: true, _developer);
        using var data = await world.AsPersonAsync(HttpMethod.Get, Entries, access);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        data.StatusCode.ShouldBe(HttpStatusCode.OK, "a refused disable changes nothing");
    }

    [Fact]
    public async Task An_unknown_account_is_404()
    {
        await using var world = await StartAsync();

        using var response = await SetDisabledAsync(world, Guid.NewGuid(), disabled: true, _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.NotFound);
    }

    /// <summary>
    /// A body that does not say which way is refused after the gate, never read as "enable" — and an unknown project is
    /// the management surface's named 404.
    /// </summary>
    [Fact]
    public async Task A_body_without_disabled_is_422_and_another_project_is_404()
    {
        await using var world = await StartAsync();
        var (access, _) = await world.SignInAsync("sub-1");
        var account = AccountOf(access);

        using var empty = await world.Api.SendAsync(HttpMethod.Put, PathOf(account), _admin, body: []);
        using var elsewhere = await world.Api.SendAsync(
            HttpMethod.Put, $"/management/projects/no-such-project/accounts/{account}/disabled", _admin, body: Flag(true));
        using var data = await world.AsPersonAsync(HttpMethod.Get, Entries, access);

        empty.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await empty.ReadTextAsync());
        elsewhere.StatusCode.ShouldBe(HttpStatusCode.NotFound, await elsewhere.ReadTextAsync());
        data.StatusCode.ShouldBe(HttpStatusCode.OK, "neither refusal disabled anyone");
    }

    /// <summary>S1: the Management API reads no Bearer token, so the person's own token on the route is an anonymous caller.</summary>
    [Fact]
    public async Task A_bearer_token_never_reaches_the_route()
    {
        await using var world = await StartAsync();
        var (access, _) = await world.SignInAsync("sub-1");
        var account = AccountOf(access);

        using var asPerson = await world.AsPersonAsync(HttpMethod.Put, PathOf(account), access, Flag(true));
        using var anonymous = await world.Api.SendAsync(HttpMethod.Put, PathOf(account), body: Flag(true));
        using var data = await world.AsPersonAsync(HttpMethod.Get, Entries, access);

        asPerson.StatusCode.ShouldBe(anonymous.StatusCode, await asPerson.ReadTextAsync());
        asPerson.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        data.StatusCode.ShouldBe(HttpStatusCode.OK, "the person did not disable themselves");
    }

    private static Task<AccountsWorld> StartAsync()
        => AccountsWorld.StartAsync(_engine, descriptor: Descriptor, keys: [_admin, _developer]);

    private static Task<HttpResponseMessage> SetDisabledAsync(AccountsWorld world, Guid account, bool disabled, TestApiKey key)
        => world.Api.SendAsync(HttpMethod.Put, PathOf(account), key, body: Flag(disabled));

    private static string PathOf(Guid account) => $"/management/projects/time-tracker/accounts/{account}/disabled";

    private static JsonObject Flag(bool disabled) => new() { ["disabled"] = disabled };

    private static Guid AccountOf(string access) => Guid.Parse(new JsonWebToken(access).Subject);
}
