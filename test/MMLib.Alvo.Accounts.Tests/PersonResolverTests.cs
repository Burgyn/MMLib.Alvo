using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tests.Accounts;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// A bearer token is a person (spec §5.7, S2 D13, D15, D10; S3 plan D4, D5): level 1's rules over the Data API as
/// <c>@user.id</c>, no tenant; level 3's tenant header acting in a membership with its tenant roles, and refusing the whole
/// principal for any tenant that is not one; a dead session, a removed member and a dropped role counting on the next
/// request; and the token validated in memory with one store read per request, membership included.
/// </summary>
public sealed class PersonResolverTests
{
    private const string Entries = "/api/time_entries";

    private const string Jobs = "/api/jobs";

    private const string TeamTracker = "team-tracker.alvo.json";

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
        counting!.Calls.Reset();

        for (var request = 0; request < 20; request++)
        {
            using var response = await world.AsPersonAsync(HttpMethod.Get, Entries, access);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        counting.SessionReads.ShouldBe(20);
        counting.KeyReads.ShouldBe(0);
    }

    [Fact]
    public async Task A_header_naming_a_membership_acts_in_it_with_its_tenant_roles()
    {
        await using var world = await TeamAsync();
        var (access, _) = await world.SignInAsync("ada");
        var tenant = await SharedTenantAsync(world, await world.UserIdAsync(access));

        var caller = await WhoAmIAsync(world, access, tenant);
        using var created = await world.AsPersonAsync(HttpMethod.Post, Jobs, access, new JsonObject { ["title"] = "Fix", ["tenant_id"] = tenant.Value }, tenant.Value.ToString());

        caller["tenant"]!.GetValue<Guid>().ShouldBe(tenant.Value);
        Names(caller["tenantRoles"]).ShouldBe(["dispatcher"]);
        Names(caller["roles"]).ShouldBe(["authenticated"], "a tenant role never becomes a project role");
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await created.ReadJsonObjectAsync())["tenant_id"]!.GetValue<Guid>().ShouldBe(tenant.Value);
    }

    [Fact]
    public async Task A_header_naming_a_tenant_the_person_is_not_in_refuses_the_principal()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var bobs = await SharedTenantAsync(world, await world.UserIdAsync(bob));
        var targets = new[] { bobs.Value, (await world.PersonalTenantAsync(bob)).Value, Guid.NewGuid() };

        var bodies = new List<string>();
        foreach (var target in targets)
        {
            using var response = await world.AsPersonAsync(HttpMethod.Get, Jobs, ada, tenant: target.ToString());
            await InvalidTokenAsync(response);
            bodies.Add(await response.ReadTextAsync());
        }

        bodies.Distinct(StringComparer.Ordinal).Count().ShouldBe(1, "another's shared tenant, another's personal tenant and no tenant read alike");
    }

    [Fact]
    public async Task Without_self_service_a_header_still_refuses_the_principal()
    {
        await using var world = await TeamAsync();
        var (access, _) = await world.SignInAsync("ada");
        var tenant = await SharedTenantAsync(world, await world.UserIdAsync(access));
        (await WhoAmIAsync(world, access, tenant))["tenant"]!.GetValue<Guid>().ShouldBe(tenant.Value);

        await world.ReapplyAsync(descriptor => descriptor["tenancy"]!.AsObject().Remove("selfService"));
        using var named = await world.AsPersonAsync(HttpMethod.Get, "/whoami", access, tenant: tenant.Value.ToString());
        var unnamed = await WhoAmIAsync(world, access);

        await InvalidTokenAsync(named);
        unnamed["tenant"].ShouldBeNull("not even the personal tenant is honoured");
        unnamed["tenantRoles"]!.AsArray().ShouldBeEmpty();
    }

    /// <summary>Review Focus 1: a role dropped from <c>assignableRoles</c> counts no more on the next request.</summary>
    [Fact]
    public async Task A_role_dropped_from_assignable_roles_is_gone_on_the_next_request()
    {
        await using var world = await TeamAsync();
        var (access, _) = await world.SignInAsync("ada");
        var tenant = await SharedTenantAsync(world, await world.UserIdAsync(access));
        Names((await WhoAmIAsync(world, access, tenant))["tenantRoles"]).ShouldBe(["dispatcher"]);

        await world.ReapplyAsync(descriptor =>
        {
            var selfService = descriptor["tenancy"]!["selfService"]!.AsObject();
            selfService["assignableRoles"] = new JsonArray("technician");
            selfService["createTenants"]!["creatorRoles"] = new JsonArray();
        });
        var after = await WhoAmIAsync(world, access, tenant);

        after["tenant"]!.GetValue<Guid>().ShouldBe(tenant.Value, "the membership stands");
        after["tenantRoles"]!.AsArray().ShouldBeEmpty("its stored dispatcher is no longer a tenant role");
    }

    /// <summary>Review Focus 1: a demoted member lacks the role, and a removed one is refused, on the next request.</summary>
    [Fact]
    public async Task A_removed_member_is_refused_on_the_next_request()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var owner = await world.UserIdAsync(ada);
        var member = await world.UserIdAsync(bob);
        var tenant = await SharedTenantAsync(world, owner);
        await world.AddMemberAsync(tenant, member, ["dispatcher", "technician"]);
        Names((await WhoAmIAsync(world, bob, tenant))["tenantRoles"]).ShouldBe(["dispatcher", "technician"]);

        (await world.Tenants.UpdateMemberAsync(new MemberUpdate(tenant, owner, member, ["technician"], null, AccountsWorld.Start), Ct))
            .ShouldBe(MemberChangeStatus.Changed);
        var demoted = await WhoAmIAsync(world, bob, tenant);
        (await world.Tenants.RemoveMemberAsync(new MemberRemoval(tenant, owner, member, AccountsWorld.Start), Ct))
            .ShouldBe(MemberChangeStatus.Changed);
        using var removed = await world.AsPersonAsync(HttpMethod.Get, Jobs, bob, tenant: tenant.Value.ToString());

        Names(demoted["tenantRoles"]).ShouldBe(["technician"]);
        await InvalidTokenAsync(removed);
    }

    /// <summary>Spec §14 AC2: a request naming a tenant costs one store read — the session and the membership together.</summary>
    [Fact]
    public async Task A_request_naming_a_tenant_is_one_store_read()
    {
        CountingAccountStore? accounts = null;
        CountingTenantStore? tenants = null;
        await using var world = await TeamAsync(services =>
        {
            services.Decorate<IAccountStore>(inner => accounts = new CountingAccountStore(inner));
            services.Decorate<ITenantStore>(inner => tenants = new CountingTenantStore(inner));
        });
        var (access, _) = await world.SignInAsync("ada");
        var tenant = await SharedTenantAsync(world, await world.UserIdAsync(access));
        accounts!.Calls.Reset();
        tenants!.Calls.Reset();

        for (var request = 0; request < 20; request++)
        {
            using var response = await world.AsPersonAsync(HttpMethod.Get, Jobs, access, tenant: tenant.Value.ToString());
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        }

        accounts.SessionReads.ShouldBe(20);
        accounts.Calls.Total.ShouldBe(20, "nothing else on the account store");
        tenants.Calls.Total.ShouldBe(0, "the membership rode on the session read");
    }

    [Fact]
    public async Task A_refused_tenant_costs_no_read_when_self_service_is_off()
    {
        CountingAccountStore? accounts = null;
        CountingTenantStore? tenants = null;
        await using var world = await AccountsWorld.StartAsync(_engine, services: services =>
        {
            services.Decorate<IAccountStore>(inner => accounts = new CountingAccountStore(inner));
            services.Decorate<ITenantStore>(inner => tenants = new CountingTenantStore(inner));
        });
        var (access, _) = await world.SignInAsync("ada");
        accounts!.Calls.Reset();

        for (var request = 0; request < 20; request++)
        {
            using var response = await world.AsPersonAsync(HttpMethod.Get, Entries, access, tenant: Guid.NewGuid().ToString());
            await InvalidTokenAsync(response);
        }

        accounts.Calls.Total.ShouldBe(0);
        (tenants?.Calls.Total ?? 0).ShouldBe(0, "the registry is not even resolved");
    }

    internal static async Task InvalidTokenAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.ReadTextAsync());
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer error=\"invalid_token\"");
        (await response.ReadJsonObjectAsync())["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.BaseUri + AlvoProblemTypes.Unauthenticated);
    }

    private static JsonObject Entry(int minutes) => new() { ["minutes"] = minutes };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Names(JsonNode? roles) =>
        [.. roles!.AsArray().Select(role => role!.GetValue<string>()).Order(StringComparer.Ordinal)];

    /// <summary>A world over <c>team-tracker</c>, with <c>/whoami</c> mapped.</summary>
    private static Task<AccountsWorld> TeamAsync(Action<IServiceCollection>? services = null) =>
        AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, services: services, app: MapWhoAmI);

    /// <summary>A shared tenant <paramref name="creator"/> creates, as <c>POST /tenants</c> will, holding <c>dispatcher</c>.</summary>
    private static async Task<TenantId> SharedTenantAsync(AccountsWorld world, UserId creator)
    {
        var tenant = TenantId.New();
        var outcome = await world.Tenants.CreateSharedAsync(
            new CreateTenantRequest(tenant, "Acme", creator, ["dispatcher"], 2, AccountsWorld.Start), Ct);
        outcome.Status.ShouldBe(CreateTenantStatus.Created);
        return tenant;
    }

    private static async Task<Guid> AccountIdAsync(AccountsWorld world, string access)
    {
        using var user = await world.AsPersonAsync(HttpMethod.Get, "/auth/v1/user", access);
        user.StatusCode.ShouldBe(HttpStatusCode.OK, await user.ReadTextAsync());
        return (await user.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    /// <summary>The caller <paramref name="access"/>'s person is published as on <c>/whoami</c>, with <paramref name="tenant"/> named.</summary>
    /// <param name="world">A world that mapped <see cref="MapWhoAmI"/>.</param>
    /// <param name="access">The access token.</param>
    /// <param name="tenant">The tenant to name, or none.</param>
    internal static async Task<JsonObject> WhoAmIAsync(AccountsWorld world, string access, TenantId? tenant = null)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Get, "/whoami", access, tenant: tenant?.Value.ToString());
        var body = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return body;
    }

    /// <summary>Maps <c>/whoami</c>, which answers the caller the request was published as.</summary>
    /// <param name="app">The host.</param>
    internal static void MapWhoAmI(WebApplication app) =>
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

    /// <summary>An account store that counts every call it serves, by member.</summary>
    private sealed class CountingAccountStore(IAccountStore inner) : IAccountStore
    {
        internal CallCounts Calls { get; } = new();

        internal int SessionReads => Calls[nameof(ReadCallerAsync)];

        internal int KeyReads => Calls[nameof(EnsureKeyAsync)] + Calls[nameof(ReadKeysAsync)];

        public Task<CallerRead> ReadCallerAsync(CallerQuery query, CancellationToken ct) =>
            Calls.Count(nameof(ReadCallerAsync), inner.ReadCallerAsync(query, ct));

        public Task<StoredKey> EnsureKeyAsync(KeyCandidate candidate, CancellationToken ct) =>
            Calls.Count(nameof(EnsureKeyAsync), inner.EnsureKeyAsync(candidate, ct));

        public Task<IReadOnlyList<StoredKey>> ReadKeysAsync(string purpose, DateTimeOffset now, CancellationToken ct) =>
            Calls.Count(nameof(ReadKeysAsync), inner.ReadKeysAsync(purpose, now, ct));

        public Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct) =>
            Calls.Count(nameof(SignInAsync), inner.SignInAsync(request, ct));

        public Task<AccountView?> GetAccountAsync(UserId account, CancellationToken ct) =>
            Calls.Count(nameof(GetAccountAsync), inner.GetAccountAsync(account, ct));

        public Task<bool> SetDisplayNameAsync(UserId account, string name, CancellationToken ct) =>
            Calls.Count(nameof(SetDisplayNameAsync), inner.SetDisplayNameAsync(account, name, ct));

        public Task<RefreshOutcome> RefreshAsync(RefreshRequest request, CancellationToken ct) =>
            Calls.Count(nameof(RefreshAsync), inner.RefreshAsync(request, ct));

        public Task<bool> RevokeSessionAsync(Guid session, string reason, DateTimeOffset now, CancellationToken ct) =>
            Calls.Count(nameof(RevokeSessionAsync), inner.RevokeSessionAsync(session, reason, now, ct));

        public Task<bool> SetDisabledAsync(UserId account, bool disabled, DateTimeOffset now, CancellationToken ct) =>
            Calls.Count(nameof(SetDisabledAsync), inner.SetDisabledAsync(account, disabled, now, ct));
    }

    /// <summary>A tenant registry that counts every call it serves, by member.</summary>
    private sealed class CountingTenantStore(ITenantStore inner) : ITenantStore
    {
        internal CallCounts Calls { get; } = new();

        public Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct) =>
            Calls.Count(nameof(CreateSharedAsync), inner.CreateSharedAsync(request, ct));

        public Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct) =>
            Calls.Count(nameof(ListMembershipsAsync), inner.ListMembershipsAsync(user, ct));

        public Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct) =>
            Calls.Count(nameof(FindMembershipAsync), inner.FindMembershipAsync(user, tenant, ct));

        public Task<IReadOnlyList<TenantId>> ListTenantIdsAsync(CancellationToken ct) =>
            Calls.Count(nameof(ListTenantIdsAsync), inner.ListTenantIdsAsync(ct));

        public Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct) =>
            Calls.Count(nameof(RenameAsync), inner.RenameAsync(tenant, actor, name, ct));

        public Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct) =>
            Calls.Count(nameof(ListMembersAsync), inner.ListMembersAsync(tenant, actor, ct));

        public Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct) =>
            Calls.Count(nameof(UpdateMemberAsync), inner.UpdateMemberAsync(update, ct));

        public Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct) =>
            Calls.Count(nameof(RemoveMemberAsync), inner.RemoveMemberAsync(removal, ct));
    }

    /// <summary>Calls counted by member name.</summary>
    private sealed class CallCounts
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        internal int this[string member] => _counts.GetValueOrDefault(member);

        internal int Total => _counts.Values.Sum();

        internal Task<T> Count<T>(string member, Task<T> call)
        {
            _counts.AddOrUpdate(member, 1, (_, count) => count + 1);
            return call;
        }

        internal void Reset() => _counts.Clear();
    }
}
