using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Accounts;

/// <summary>
/// App tokens end to end, the same facts on SQLite and on PostgreSQL (spec §7.1, §6.2, §6.4, §14 AC3–AC4; S5 Review Focus
/// 1, 3, 4 and 5): a personal token acts as its person with the roles of their current membership and dies with it, for
/// good; a service token acts as the tenant with fixed roles and dies with its minter's ownership; a ban kills both kinds
/// and an enable revives neither; a token never acts outside its tenant, never reaches global data or management with a
/// tenant role, and the descriptor moving under a live token counts on its next request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each engine contributes only <see cref="Engine"/></b>, as in <see cref="InvitationEngineFacts"/>. Everything here goes
/// over HTTP, the way an integration would: a person signs in, mints over <c>POST /auth/v1/tokens</c>, and the token is
/// presented as <c>X-Alvo-Api-Key</c>. A second member joins by invitation, the real path, so a rejoin is a real rejoin.
/// </para>
/// <para>
/// <b>Where the plan's wording and the Data API disagree, the Data API wins</b>, as in S3: a refused <c>PATCH</c> of a global
/// row is 404, not 403 — an <c>update</c> rule filters the rows a caller may change (S3 accounts.md, recorded deviation).
/// </para>
/// </remarks>
public abstract class AppTokenEngineFacts
{
    private const string Tenants = "/auth/v1/tenants";

    private const string Accept = "/auth/v1/invitations/accept";

    private const string Jobs = "/api/jobs";

    private const string Regions = "/api/regions";

    private const string Entries = "/api/time_entries";

    private const string TeamTracker = "team-tracker.alvo.json";

    private const string ManagementDetail =
        "Personal and service tokens are app credentials and never manage a project. Use an operator session or a configured API key.";

    private static readonly Guid _integrationTenant = Guid.Parse("7a1c0de0-0000-4000-8000-000000000005");

    private static readonly TestApiKey _integration = new("app-token-integration", ["authenticated", "dispatcher"], ["*:read", "*:write"], _integrationTenant);

    /// <summary>Gets the engine every world in this suite runs on.</summary>
    protected abstract AlvoApiEngine Engine { get; }

    /// <summary>
    /// S5 Review Focus 1: Carol's integration runs on her personal token. Demoted, it loses exactly the role she lost on its
    /// next request; removed, it is 401; invited back, she is a member again — and the old token stays dead, because her
    /// removal revoked it in its own transaction (S5 plan D10).
    /// </summary>
    [Fact]
    public async Task Personal_token_end_to_end()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        var (carol, _) = await world.SignInAsync("carol", email: "carol@gmail.com");
        var carolId = await world.UserIdAsync(carol);
        await JoinedAsync(world, ada, carol, acme, Invitation("dispatcher"));
        var token = await MintedAsync(world, carol, Personal(world, acme, "jobs:read", "jobs:write"));

        using (var created = await JobAsync(world, token, acme, "Boiler service"))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
            (await created.ReadJsonObjectAsync())["created_by"]!.GetValue<Guid>().ShouldBe(carolId.Value, "a personal token acts as its person");
        }

        await PatchedAsync(world, ada, MemberPath(acme, carolId), new JsonObject { ["roles"] = new JsonArray("technician") });
        await ExpectAsync(JobAsync(world, token, acme, "After the demotion"), HttpStatusCode.Forbidden);
        (await JobsAsync(world, token, acme)).ShouldHaveSingleItem("a technician still reads Acme's jobs");

        await RemovedAsync(world, ada, acme, carolId);
        await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, Jobs, token, Header(acme)), HttpStatusCode.Unauthorized);

        await JoinedAsync(world, ada, carol, acme, Invitation("dispatcher"));
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Get, Jobs, carol, tenant: Header(acme)), HttpStatusCode.OK);
        await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, Jobs, token, Header(acme)), HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A service token acts as the tenant — its own user id fills <c>created_by</c> — until its minter stops being an owner;
    /// then it is 401, and a remaining owner mints the replacement.
    /// </summary>
    [Fact]
    public async Task Service_token_end_to_end()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var adaId = await world.UserIdAsync(ada);
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        var service = await MintedAsync(world, ada, Service(world, acme, ["dispatcher"], "jobs:read", "jobs:write"));

        using (var created = await JobAsync(world, service, acme, "Fix the sink"))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
            (await created.ReadJsonObjectAsync())["created_by"]!.GetValue<Guid>().ShouldNotBe(adaId.Value, "a service token acts as itself");
        }

        (await JobsAsync(world, service, acme)).ShouldHaveSingleItem();
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        await JoinedAsync(world, ada, bob, acme, Invitation("dispatcher", isOwner: true));

        await PatchedAsync(world, bob, MemberPath(acme, adaId), new JsonObject { ["isOwner"] = false });

        await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, Jobs, service, Header(acme)), HttpStatusCode.Unauthorized);
        var replacement = await MintedAsync(world, bob, Service(world, acme, ["dispatcher"], "jobs:read", "jobs:write"));
        await ExpectAsync(JobAsync(world, replacement, acme, "Fixed again"), HttpStatusCode.Created);
        (await JobsAsync(world, replacement, acme)).Count.ShouldBe(2);
    }

    /// <summary>
    /// Spec §3's level 1 with a token: a tenantless personal token reads and writes as its person, so <c>created_by ==
    /// @user.id</c> keeps another person's rows out of its reach exactly as it does for the person's session.
    /// </summary>
    [Fact]
    public async Task Level_one_personal_token()
    {
        await using var world = await AccountsWorld.StartAsync(Engine);
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var adaId = await world.UserIdAsync(ada);
        var token = await MintedAsync(world, ada, Personal(world, null, "time_entries:read", "time_entries:write"));
        Guid bobs;
        using (var bobEntry = await world.AsPersonAsync(HttpMethod.Post, Entries, bob, new JsonObject { ["minutes"] = 45 }))
        {
            bobEntry.StatusCode.ShouldBe(HttpStatusCode.Created, await bobEntry.ReadTextAsync());
            bobs = (await bobEntry.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
        }

        using (var created = await world.AsTokenAsync(HttpMethod.Post, Entries, token, body: new { minutes = 30 }))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
            (await created.ReadJsonObjectAsync())["created_by"]!.GetValue<Guid>().ShouldBe(adaId.Value);
        }

        using var listed = await world.AsTokenAsync(HttpMethod.Get, Entries, token);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.ReadTextAsync());
        (await listed.ReadJsonObjectAsync())["items"]!.AsArray().ShouldHaveSingleItem()!["minutes"]!.GetValue<int>().ShouldBe(30);
        await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, $"{Entries}/{bobs:D}", token), HttpStatusCode.NotFound);
        await ExpectAsync(world.AsTokenAsync(HttpMethod.Patch, $"{Entries}/{bobs:D}", token, body: new { minutes = 1 }), HttpStatusCode.NotFound);
    }

    /// <summary>
    /// S5 plan D10, beyond §7.1 for the service half: an operator's ban revokes every token the account minted, in the ban's
    /// transaction; enabling the account revives none — its owner sees both listed as revoked and mints new ones.
    /// </summary>
    [Fact]
    public async Task A_banned_person_s_tokens_die_and_stay_dead()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var adaId = await world.UserIdAsync(ada);
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        string[] tokens =
        [
            await MintedAsync(world, ada, Personal(world, acme, "jobs:read")),
            await MintedAsync(world, ada, Service(world, acme, ["dispatcher"], "jobs:read")),
        ];
        await EachAsync(world, tokens, acme, HttpStatusCode.OK);

        await DisabledAsync(world, adaId, disabled: true);
        await EachAsync(world, tokens, acme, HttpStatusCode.Unauthorized);

        await DisabledAsync(world, adaId, disabled: false);
        await EachAsync(world, tokens, acme, HttpStatusCode.Unauthorized);
        var (again, _) = await world.SignInAsync("ada");
        using var list = await world.AsPersonAsync(HttpMethod.Get, "/auth/v1/tokens", again);
        (await list.ReadJsonArrayAsync()).Select(item => item["status"]!.GetValue<string>()).ShouldBe(["revoked", "revoked"]);
    }

    /// <summary>
    /// S5 Review Focus 3: a token's tenant is fixed at mint and <c>X-Alvo-Tenant</c> only confirms it (spec §6.6). Naming
    /// another tenant the person is also in refuses the whole principal; naming none acts in the token's own.
    /// </summary>
    [Fact]
    public async Task A_token_never_acts_outside_its_tenant()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        var globex = await CreatedTenantAsync(world, ada, "Globex");
        await CreatedJobAsync(world, ada, acme, "In Acme");
        await CreatedJobAsync(world, ada, globex, "In Globex");
        string[] tokens =
        [
            await MintedAsync(world, ada, Personal(world, acme, "jobs:read")),
            await MintedAsync(world, ada, Service(world, acme, ["dispatcher"], "jobs:read")),
        ];

        foreach (var token in tokens)
        {
            await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, Jobs, token, Header(globex)), HttpStatusCode.Unauthorized);
            (await JobsAsync(world, token, acme)).ShouldHaveSingleItem()!["title"]!.GetValue<string>().ShouldBe("In Acme");
            (await JobsAsync(world, token, null)).ShouldHaveSingleItem()!["title"]!.GetValue<string>().ShouldBe("In Acme");
        }
    }

    /// <summary>
    /// S5 Review Focus 4, spec §6.2's table through a token: a <c>dispatcher</c> service token holds a tenant role, which
    /// counts on Acme's scoped rows only. It cannot create a region, nor change one through <c>jobs.exists(…)</c> — true
    /// here, since an Acme job references the region — though its scopes allow both; and Management refuses it outright
    /// (S5 plan D9). The integration key's project <c>dispatcher</c> is admitted, so the rule is live.
    /// </summary>
    [Fact]
    public async Task A_service_token_s_tenant_role_never_reaches_global_data_or_management()
    {
        await using var world = await TeamAsync(keys: [_integration]);
        var region = await SeededRegionAsync(world);
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        await CreatedJobAsync(world, ada, acme, "In the north", region);
        var service = await MintedAsync(
            world, ada, Service(world, acme, ["dispatcher"], "jobs:read", "jobs:write", "regions:read", "regions:write"));

        using var created = await world.AsTokenAsync(HttpMethod.Post, Regions, service, Header(acme), new { code = "SOUTH" });
        using var changed = await world.AsTokenAsync(HttpMethod.Patch, $"{Regions}/{region:D}", service, Header(acme), new { code = "MINE" });
        using var management = await world.AsTokenAsync(HttpMethod.Get, "/management/projects/team-tracker/descriptor", service);
        using var job = await JobAsync(world, service, acme, "Dispatched by the token");
        using var project = await world.Api.SendAsync(
            HttpMethod.Patch, $"{Regions}/{region:D}", _integration, body: new JsonObject { ["code"] = "NORTH2" });

        created.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await created.ReadTextAsync());
        changed.StatusCode.ShouldBe(HttpStatusCode.NotFound, await changed.ReadTextAsync());
        management.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await management.ReadTextAsync());
        (await management.ReadProblemDetailAsync()).ShouldBe(ManagementDetail);
        job.StatusCode.ShouldBe(HttpStatusCode.Created, "the tenant role is held, on the tenant's scoped rows");
        project.StatusCode.ShouldBe(HttpStatusCode.OK, await project.ReadTextAsync());
    }

    /// <summary>
    /// S5 Review Focus 5: the applied descriptor decides on every request (S5 plan D5, D11). A role dropped from
    /// <c>assignableRoles</c> is gone from a service token; <c>service: false</c> refuses it and <c>true</c> revives it;
    /// without <c>selfService</c> a personal token in a tenant is refused, and a tenantless one still works.
    /// </summary>
    [Fact]
    public async Task The_descriptor_moves_under_live_tokens()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        var service = await MintedAsync(world, ada, Service(world, acme, ["dispatcher"], "jobs:read", "jobs:write"));
        var personal = await MintedAsync(world, ada, Personal(world, acme, "jobs:read"));
        var tenantless = await MintedAsync(world, ada, Personal(world, null, "regions:read"));
        await ExpectAsync(JobAsync(world, service, acme, "Before"), HttpStatusCode.Created);

        await world.ReapplyAsync(descriptor => OnlyTechnicianAssignable(descriptor["tenancy"]!["selfService"]!));
        await ExpectAsync(JobAsync(world, service, acme, "Dispatcher dropped"), HttpStatusCode.Forbidden);

        await world.ReapplyAsync(descriptor => descriptor["auth"]!["accessTokens"]!["service"] = false);
        await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, Jobs, service, Header(acme)), HttpStatusCode.Unauthorized);
        await world.ReapplyAsync(descriptor => descriptor["auth"]!["accessTokens"]!["service"] = true);
        await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, Jobs, service, Header(acme)), HttpStatusCode.OK);

        await world.ReapplyAsync(AccountsWorld.WithoutSelfService);
        await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, Jobs, personal, Header(acme)), HttpStatusCode.Unauthorized);
        await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, Regions, tenantless), HttpStatusCode.OK);
    }

    private static string Header(Guid tenant) => tenant.ToString("D");

    private static string MemberPath(Guid tenant, UserId member) => $"{Tenants}/{tenant:D}/members/{member.Value:D}";

    private Task<AccountsWorld> TeamAsync(IReadOnlyList<TestApiKey>? keys = null)
        => AccountsWorld.StartAsync(Engine, descriptor: TeamTracker, keys: keys);

    /// <summary><c>creatorRoles</c> must stay within <c>assignableRoles</c>, so both drop <c>dispatcher</c> together.</summary>
    private static void OnlyTechnicianAssignable(JsonNode selfService)
    {
        selfService["assignableRoles"] = new JsonArray("technician");
        selfService["createTenants"]!["creatorRoles"] = new JsonArray("technician");
    }

    private static JsonObject Invitation(string role, bool isOwner = false)
        => new() { ["roles"] = new JsonArray(role), ["isOwner"] = isOwner };

    private static JsonObject Personal(AccountsWorld world, Guid? tenant, params string[] scopes)
        => TokenBody(world, "personal", tenant, null, scopes);

    private static JsonObject Service(AccountsWorld world, Guid tenant, string[] roles, params string[] scopes)
        => TokenBody(world, "service", tenant, roles, scopes);

    private static JsonObject TokenBody(AccountsWorld world, string kind, Guid? tenant, string[]? roles, string[] scopes)
    {
        var body = new JsonObject
        {
            ["name"] = $"{kind} integration",
            ["kind"] = kind,
            ["scopes"] = new JsonArray([.. scopes.Select(scope => JsonValue.Create(scope))]),
            ["expiresAt"] = world.Clock.GetUtcNow().AddDays(7).ToString("O", CultureInfo.InvariantCulture),
        };
        if (tenant is { } id)
        {
            body["tenantId"] = Header(id);
        }

        if (roles is not null)
        {
            body["roles"] = new JsonArray([.. roles.Select(role => JsonValue.Create(role))]);
        }

        return body;
    }

    private static async Task<string> MintedAsync(AccountsWorld world, string access, JsonObject body)
    {
        using var response = await world.MintAsync(access, body);
        var minted = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, minted.ToJsonString());
        return minted["token"]!.GetValue<string>();
    }

    private static async Task<Guid> CreatedTenantAsync(AccountsWorld world, string access, string name)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Post, Tenants, access, new JsonObject { ["name"] = name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["tenantId"]!.GetValue<Guid>();
    }

    /// <summary>Ada (<paramref name="owner"/>) invites, and <paramref name="invitee"/> accepts.</summary>
    private static async Task JoinedAsync(AccountsWorld world, string owner, string invitee, Guid tenant, JsonObject invitation)
    {
        using var invited = await world.AsPersonAsync(HttpMethod.Post, $"{Tenants}/{tenant:D}/invitations", owner, invitation);
        var created = await invited.ReadJsonObjectAsync();
        invited.StatusCode.ShouldBe(HttpStatusCode.Created, created.ToJsonString());
        await ExpectAsync(
            world.AsPersonAsync(HttpMethod.Post, Accept, invitee, new JsonObject { ["code"] = created["code"]!.GetValue<string>() }),
            HttpStatusCode.OK);
    }

    private static Task PatchedAsync(AccountsWorld world, string owner, string path, JsonObject patch)
        => ExpectAsync(world.AsPersonAsync(HttpMethod.Patch, path, owner, patch), HttpStatusCode.OK);

    private static Task RemovedAsync(AccountsWorld world, string owner, Guid tenant, UserId member)
        => ExpectAsync(world.AsPersonAsync(HttpMethod.Delete, MemberPath(tenant, member), owner), HttpStatusCode.NoContent);

    private static Task DisabledAsync(AccountsWorld world, UserId account, bool disabled) => ExpectAsync(
        world.Api.SendAsync(
            HttpMethod.Put,
            $"/management/projects/team-tracker/accounts/{account.Value:D}/disabled",
            AccountsWorld.Operator,
            body: new JsonObject { ["disabled"] = disabled }),
        HttpStatusCode.NoContent);

    private static async Task EachAsync(AccountsWorld world, string[] tokens, Guid tenant, HttpStatusCode status)
    {
        foreach (var token in tokens)
        {
            await ExpectAsync(world.AsTokenAsync(HttpMethod.Get, Jobs, token, Header(tenant)), status);
        }
    }

    private static async Task<Guid> SeededRegionAsync(AccountsWorld world)
    {
        using var seeded = await world.Api.SendAsync(HttpMethod.Post, Regions, _integration, body: new JsonObject { ["code"] = "NORTH" });
        seeded.StatusCode.ShouldBe(HttpStatusCode.Created, await seeded.ReadTextAsync());
        return (await seeded.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static Task<HttpResponseMessage> JobAsync(AccountsWorld world, string token, Guid tenant, string title)
        => world.AsTokenAsync(HttpMethod.Post, Jobs, token, Header(tenant), new { title, tenant_id = tenant });

    private static async Task CreatedJobAsync(AccountsWorld world, string access, Guid tenant, string title, Guid? region = null)
    {
        var body = new JsonObject { ["title"] = title, ["tenant_id"] = Header(tenant) };
        if (region is { } id)
        {
            body["region_id"] = Header(id);
        }

        await ExpectAsync(world.AsPersonAsync(HttpMethod.Post, Jobs, access, body, tenant: Header(tenant)), HttpStatusCode.Created);
    }

    private static async Task<JsonArray> JobsAsync(AccountsWorld world, string token, Guid? tenant)
    {
        using var list = await world.AsTokenAsync(HttpMethod.Get, Jobs, token, tenant is { } id ? Header(id) : null);
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.ReadTextAsync());
        return (await list.ReadJsonObjectAsync())["items"]!.AsArray();
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> sent, HttpStatusCode status)
    {
        using var response = await sent;
        response.StatusCode.ShouldBe(status, await response.ReadTextAsync());
    }
}
