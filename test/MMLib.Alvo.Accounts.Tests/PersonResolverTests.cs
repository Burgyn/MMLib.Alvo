using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Tests.Accounts;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// A bearer token is a person (spec §5.7, S2 D13, D15, D10): level 1's rules over the Data API as <c>@user.id</c>, no
/// tenant, a requested tenant refusing the whole principal, a dead session refused on its next request, and the token
/// validated in memory with one store read per request.
/// </summary>
public sealed class PersonResolverTests
{
    private const string Entries = "/api/time_entries";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task A_person_writes_and_reads_their_own_rows_with_a_bearer_token()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");

        using var created = await world.AsPersonAsync(HttpMethod.Post, Entries, access, Entry(30));
        using var list = await world.AsPersonAsync(HttpMethod.Get, Entries, access);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await created.ReadJsonObjectAsync())["created_by"]!.GetValue<Guid>().ShouldBe(await AccountIdAsync(world, access));
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.ReadTextAsync());
        (await list.ReadJsonObjectAsync())["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Another_person_sees_none_of_them()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        using var created = await world.AsPersonAsync(HttpMethod.Post, Entries, ada, Entry(30));
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

        using var list = await world.AsPersonAsync(HttpMethod.Get, Entries, bob);
        using var get = await world.AsPersonAsync(HttpMethod.Get, $"{Entries}/{id}", bob);

        (await list.ReadJsonObjectAsync())["items"]!.AsArray().ShouldBeEmpty();
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("a tenant id")]
    [InlineData("not-a-tenant")]
    public async Task A_tenant_header_refuses_the_whole_principal(string tenant)
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");

        using var response = await world.AsPersonAsync(
            HttpMethod.Get, Entries, access, tenant: tenant == "a tenant id" ? Guid.NewGuid().ToString() : tenant);

        await InvalidTokenAsync(response);
    }

    [Fact]
    public async Task The_principal_is_a_user_with_only_authenticated()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, app: MapWhoAmI);
        var (access, _) = await world.SignInAsync("sub-1");

        using var response = await world.AsPersonAsync(HttpMethod.Get, "/whoami", access);

        var body = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        var token = new JsonWebToken(access);
        body["user"]!.GetValue<string>().ShouldBe(token.Subject);
        body["actor"]!.GetValue<string>().ShouldBe(nameof(AlvoActorKind.User));
        body["roles"]!.AsArray().Select(role => role!.GetValue<string>()).ShouldBe(["authenticated"]);
        body["tenantRoles"]!.AsArray().ShouldBeEmpty();
        body["tenant"].ShouldBeNull();
        body["scopes"]!.AsArray().Select(scope => scope!.GetValue<string>()).ShouldBe(["*:Read", "*:Write"], ignoreOrder: true);
        body["keyId"]!.GetValue<string>().ShouldBe("session:" + token.GetPayloadValue<string>("sid"));
    }

    [Fact]
    public async Task A_signed_out_session_is_401_on_its_next_request()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");
        using var before = await world.AsPersonAsync(HttpMethod.Get, Entries, access);
        before.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var logout = await world.AsPersonAsync(HttpMethod.Post, "/auth/v1/logout", access);
        using var after = await world.AsPersonAsync(HttpMethod.Get, Entries, access);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await InvalidTokenAsync(after);
    }

    [Fact]
    public async Task A_descriptor_that_drops_google_refuses_live_sessions_and_refreshes()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, refresh) = await world.SignInAsync("sub-1");

        await world.ReapplyAsync(descriptor => descriptor["auth"]!.AsObject().Remove("providers"));
        using var data = await world.AsPersonAsync(HttpMethod.Get, Entries, access);
        using var refreshed = await world.RefreshAsync(refresh);
        using var exchanged = await world.ExchangeAsync(world.IdToken("sub-1"));

        await InvalidTokenAsync(data);
        await TokenExchangeTests.RefusalAsync(refreshed, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_grant");
        await TokenExchangeTests.RefusalAsync(exchanged, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_request");
    }

    [Fact]
    public async Task An_access_token_is_validated_without_reading_keys_again()
    {
        CountingAccountStore? counting = null;
        await using var world = await AccountsWorld.StartAsync(
            _engine, services: services => services.Decorate<IAccountStore>(inner => counting = new CountingAccountStore(inner)));
        var (access, _) = await world.SignInAsync("sub-1");
        counting!.Reset();

        for (var request = 0; request < 20; request++)
        {
            using var response = await world.AsPersonAsync(HttpMethod.Get, Entries, access);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        counting.SessionReads.ShouldBe(20);
        counting.KeyReads.ShouldBe(0);
    }

    internal static async Task InvalidTokenAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.ReadTextAsync());
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer error=\"invalid_token\"");
        (await response.ReadJsonObjectAsync())["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.BaseUri + AlvoProblemTypes.Unauthenticated);
    }

    private static JsonObject Entry(int minutes) => new() { ["minutes"] = minutes };

    private static async Task<Guid> AccountIdAsync(AccountsWorld world, string access)
    {
        using var user = await world.AsPersonAsync(HttpMethod.Get, "/auth/v1/user", access);
        user.StatusCode.ShouldBe(HttpStatusCode.OK, await user.ReadTextAsync());
        return (await user.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static void MapWhoAmI(WebApplication app) =>
        app.MapGet("/whoami", (AlvoCaller caller, IAlvoContextAccessor accessor) => Results.Json(new
        {
            user = caller.Context.User.Value.ToString(),
            actor = caller.Context.ActorKind.ToString(),
            roles = caller.Context.Roles.Select(role => role.Name),
            tenantRoles = caller.Context.TenantRoles.Select(role => role.Name),
            tenant = caller.Context.Tenant?.Value,
            scopes = accessor.Principal!.Scopes.Select(scope => $"{scope.Entity}:{scope.Access}"),
            keyId = accessor.Principal!.KeyId,
        })).WithAlvoCaller();

    /// <summary>An account store that counts the session reads and the key reads it serves.</summary>
    private sealed class CountingAccountStore(IAccountStore inner) : IAccountStore
    {
        private int _sessionReads;

        private int _keyReads;

        internal int SessionReads => Volatile.Read(ref _sessionReads);

        internal int KeyReads => Volatile.Read(ref _keyReads);

        internal void Reset()
        {
            Interlocked.Exchange(ref _sessionReads, 0);
            Interlocked.Exchange(ref _keyReads, 0);
        }

        public Task<bool> IsSessionLiveAsync(UserId account, Guid session, DateTimeOffset now, CancellationToken ct)
        {
            Interlocked.Increment(ref _sessionReads);
            return inner.IsSessionLiveAsync(account, session, now, ct);
        }

        public Task<StoredKey> EnsureKeyAsync(KeyCandidate candidate, CancellationToken ct)
        {
            Interlocked.Increment(ref _keyReads);
            return inner.EnsureKeyAsync(candidate, ct);
        }

        public Task<IReadOnlyList<StoredKey>> ReadKeysAsync(string purpose, DateTimeOffset now, CancellationToken ct)
        {
            Interlocked.Increment(ref _keyReads);
            return inner.ReadKeysAsync(purpose, now, ct);
        }

        public Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct) => inner.SignInAsync(request, ct);

        public Task<AccountView?> GetAccountAsync(UserId account, CancellationToken ct) => inner.GetAccountAsync(account, ct);

        public Task<bool> SetDisplayNameAsync(UserId account, string name, CancellationToken ct)
            => inner.SetDisplayNameAsync(account, name, ct);

        public Task<RefreshOutcome> RefreshAsync(RefreshRequest request, CancellationToken ct) => inner.RefreshAsync(request, ct);

        public Task<bool> RevokeSessionAsync(Guid session, string reason, DateTimeOffset now, CancellationToken ct)
            => inner.RevokeSessionAsync(session, reason, now, ct);

        public Task<bool> SetDisabledAsync(UserId account, bool disabled, DateTimeOffset now, CancellationToken ct)
            => inner.SetDisabledAsync(account, disabled, now, ct);
    }
}
