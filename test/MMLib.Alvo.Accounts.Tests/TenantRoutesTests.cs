using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tests.Accounts;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// A person's tenants (spec §6.3, S3 plan D1, D7, D8, D9, D14): listed, created with a server-chosen id under the cap and
/// the creation budget, renamed by an owner — only by a signed-in person, never confirming a tenant to a stranger, and
/// answering <c>feature-disabled</c> while the descriptor leaves the feature off.
/// </summary>
public sealed class TenantRoutesTests
{
    internal const string Tenants = "/auth/v1/tenants";

    private const string TeamTracker = "team-tracker.alvo.json";

    private const string Allowed = "https://app.example.test";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task Creating_a_tenant_answers_201_with_the_owner_membership()
    {
        await using var world = await TeamAsync();
        var (access, _) = await world.SignInAsync("ada");

        using var response = await CreateAsync(world, access, "  Acme  ");

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = await response.ReadJsonObjectAsync();
        var tenant = body["tenantId"]!.GetValue<Guid>();
        response.Headers.Location!.OriginalString.ShouldBe($"{Tenants}/{tenant:D}");
        body.ToJsonString().ShouldBe($$"""{"tenantId":"{{tenant:D}}","name":"Acme","kind":"shared","roles":["dispatcher"],"isOwner":true}""");
    }

    [Fact]
    public async Task The_list_holds_the_personal_and_the_shared_tenants()
    {
        await using var world = await TeamAsync();
        var (access, _) = await world.SignInAsync("ada");
        var shared = await CreatedAsync(world, access, "Acme");

        using var response = await world.AsPersonAsync(HttpMethod.Get, Tenants, access);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var personal = await world.PersonalTenantAsync(access);
        (await response.ReadJsonArrayAsync()).Select(membership => membership.ToJsonString()).ShouldBe(
        [
            $$"""{"tenantId":"{{personal}}","name":"Ada Lovelace","kind":"personal","roles":[],"isOwner":true}""",
            $$"""{"tenantId":"{{shared:D}}","name":"Acme","kind":"shared","roles":["dispatcher"],"isOwner":true}""",
        ]);
    }

    [Fact]
    public async Task An_owner_renames_a_tenant()
    {
        await using var world = await TeamAsync();
        var (access, _) = await world.SignInAsync("ada");
        var tenant = await CreatedAsync(world, access, "Acme");

        using var renamed = await RenameAsync(world, access, tenant, "Acme Field Service");
        var listed = await world.MembershipsAsync(access);

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK, await renamed.ReadTextAsync());
        (await renamed.ReadJsonObjectAsync()).ToJsonString().ShouldBe(
            $$"""{"tenantId":"{{tenant:D}}","name":"Acme Field Service","kind":"shared","roles":["dispatcher"],"isOwner":true}""");
        listed.Single(membership => membership!["tenantId"]!.GetValue<Guid>() == tenant)!["name"]!.GetValue<string>()
            .ShouldBe("Acme Field Service");
    }

    [Fact]
    public async Task A_member_who_is_not_an_owner_cannot_rename()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var tenant = await CreatedAsync(world, ada, "Acme");
        await world.AddMemberAsync(new TenantId(tenant), await world.UserIdAsync(bob), ["technician"]);

        using var response = await RenameAsync(world, bob, tenant, "Bob's now");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        (await world.MembershipsAsync(ada)).Single(membership => membership!["kind"]!.GetValue<string>() == "shared")!["name"]!
            .GetValue<string>().ShouldBe("Acme");
    }

    [Fact]
    public async Task A_non_member_and_an_unknown_tenant_get_the_same_404()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var adas = await CreatedAsync(world, ada, "Acme");

        using var foreign = await RenameAsync(world, bob, adas, "Mine now");
        using var unknown = await RenameAsync(world, bob, Guid.NewGuid(), "Mine now");

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, await foreign.ReadTextAsync());
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await foreign.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.NotFound);
        (await foreign.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(await unknown.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task The_third_tenant_is_refused_at_a_cap_of_two()
    {
        await using var world = await TeamAsync();
        var (access, _) = await world.SignInAsync("ada");
        await CreatedAsync(world, access, "One");
        await CreatedAsync(world, access, "Two");

        using var third = await CreateAsync(world, access, "Three");

        third.StatusCode.ShouldBe(HttpStatusCode.Conflict, await third.ReadTextAsync());
        var problem = await third.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.BaseUri + AlvoProblemTypes.TenantLimit);
        problem["maxPerAccount"]!.GetValue<int>().ShouldBe(2);
        (await world.MembershipsAsync(access)).Count.ShouldBe(3, "the personal tenant and the two shared ones");
    }

    [Fact]
    public async Task Tenant_creation_answers_429_after_its_limit()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, api: api => api.RateLimits.Create = 2);
        var (access, _) = await world.SignInAsync("ada");

        using var first = await CreateAsync(world, access, "One");
        using var second = await CreateAsync(world, access, "Two");
        using var third = await CreateAsync(world, access, "Three");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, await third.ReadTextAsync());
        (await third.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.RateLimited);
        int.Parse(third.Headers.GetValues("Retry-After").ShouldHaveSingleItem(), CultureInfo.InvariantCulture).ShouldBeInRange(1, 60);
        third.Headers.CacheControl!.NoStore.ShouldBeTrue();
        for (var i = 0; i < 3; i++)
        {
            using var data = await world.AsPersonAsync(HttpMethod.Get, "/api/regions", access);
            data.StatusCode.ShouldBe(HttpStatusCode.OK, await data.ReadTextAsync());
            using var list = await world.AsPersonAsync(HttpMethod.Get, Tenants, access);
            list.StatusCode.ShouldBe(HttpStatusCode.OK, "only creating is charged");
        }
    }

    [Fact]
    public async Task Without_self_service_every_route_is_404_feature_disabled()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("ada");

        using var list = await world.AsPersonAsync(HttpMethod.Get, Tenants, access);
        using var create = await CreateAsync(world, access, "Acme");
        using var rename = await RenameAsync(world, access, Guid.NewGuid(), "Acme");

        foreach (var response in new[] { list, create, rename })
        {
            await FeatureDisabledAsync(response, "tenancy.selfService");
        }
    }

    [Fact]
    public async Task Without_create_tenants_creating_is_404_naming_its_key()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: "personal-tracker.alvo.json");
        var (access, _) = await world.SignInAsync("ada");

        using var create = await CreateAsync(world, access, "Acme");
        using var list = await world.AsPersonAsync(HttpMethod.Get, Tenants, access);

        await FeatureDisabledAsync(create, "tenancy.selfService.createTenants");
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.ReadTextAsync());
        (await list.ReadJsonArrayAsync()).ShouldHaveSingleItem()["kind"]!.GetValue<string>().ShouldBe("personal");
    }

    [Fact]
    public async Task An_api_key_is_403_and_no_credential_is_a_bearer_challenge()
    {
        var integration = new TestApiKey("integration", ["authenticated"], ["*:read", "*:write"]);
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, keys: [integration]);

        using var keyed = await world.Api.SendAsync(HttpMethod.Get, Tenants, integration);
        using var anonymous = await world.Api.SendAsync(HttpMethod.Get, Tenants);

        keyed.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await keyed.ReadTextAsync());
        (await keyed.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await anonymous.ReadTextAsync());
        anonymous.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        anonymous.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Theory]
    [InlineData("{\"name\":\"\"}", "required")]
    [InlineData("{\"name\":\"101\"}", "max-length")]
    [InlineData("{\"name\":\"a\\u0007b\"}", "invalid-value")]
    [InlineData("{\"name\":\"a\\ud800\"}", "invalid-value")]
    public async Task A_bad_name_is_a_validation_problem(string json, string code)
    {
        await using var world = await TeamAsync();
        var (access, _) = await world.SignInAsync("ada");
        var body = json == "{\"name\":\"101\"}" ? $"{{\"name\":\"{new string('a', 101)}\"}}" : json;

        using var response = await RawAsync(world, HttpMethod.Post, Tenants, access, new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await response.ReadViolationsAsync()).ShouldBe([("/name", code)]);
        (await world.MembershipsAsync(access)).Count.ShouldBe(1, "nothing was created");
    }

    [Fact]
    public async Task A_non_json_body_is_415()
    {
        await using var world = await TeamAsync();
        var (access, _) = await world.SignInAsync("ada");

        using var response = await RawAsync(
            world, HttpMethod.Post, Tenants, access, new StringContent("name=Acme", Encoding.UTF8, "application/x-www-form-urlencoded"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.UnsupportedMediaType);
    }

    [Fact]
    public async Task An_allowed_origin_gets_cors_headers_on_the_tenant_routes()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, api: api => api.Cors.Origins.Add(Allowed));
        var (access, _) = await world.SignInAsync("ada");

        using var listed = await world.Api.SendAsync(
            HttpMethod.Get, Tenants, headers: [new("Origin", Allowed), new("Authorization", $"Bearer {access}")]);
        using var refused = await world.Api.SendAsync(HttpMethod.Get, Tenants, headers: [new("Origin", Allowed)]);
        using var preflight = await world.Api.SendAsync(
            HttpMethod.Options, Tenants, headers: [new("Origin", Allowed), new("Access-Control-Request-Method", "POST")]);

        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.ReadTextAsync());
        listed.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refused.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
        preflight.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        preflight.Headers.GetValues("Access-Control-Allow-Methods").ShouldHaveSingleItem().ShouldContain("POST");
    }

    /// <summary>
    /// A creation status the route does not know is a broken invariant, raised to the host's exception pipeline (the
    /// embedded shape: it propagates; a standalone host answers 500 <c>internal</c>) — never guessed into a refusal such as
    /// 401, which would send a client to sign in again over a server defect.
    /// </summary>
    [Fact]
    public async Task An_unknown_creation_status_is_a_server_defect_not_a_refusal()
    {
        await using var world = await AccountsWorld.StartAsync(
            _engine, descriptor: TeamTracker, services: host => host.Decorate<ITenantStore>(inner => new UnknownCreation(inner)));
        var (access, _) = await world.SignInAsync("ada");

        var raised = await Should.ThrowAsync<InvalidOperationException>(() => CreateAsync(world, access, "Acme"));

        raised.Message.ShouldContain("does not know");
    }

    /// <summary>Creates a tenant named <paramref name="name"/> as <paramref name="access"/>'s person, asserting it was created.</summary>
    internal static async Task<Guid> CreatedAsync(AccountsWorld world, string access, string name)
    {
        using var response = await CreateAsync(world, access, name);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["tenantId"]!.GetValue<Guid>();
    }

    /// <summary>Sends <paramref name="content"/> verbatim as <paramref name="access"/>'s person.</summary>
    internal static Task<HttpResponseMessage> RawAsync(
        AccountsWorld world, HttpMethod method, string path, string access, HttpContent content) =>
        world.Api.SendRawAsync(method, path, content: content, headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {access}")]);

    /// <summary>Asserts <paramref name="response"/> is 404 <c>feature-disabled</c> naming <paramref name="key"/>.</summary>
    internal static async Task FeatureDisabledAsync(HttpResponseMessage response, string key)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.ReadTextAsync());
        var problem = await response.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.BaseUri + AlvoProblemTypes.FeatureDisabled);
        problem["descriptorKey"]!.GetValue<string>().ShouldBe(key);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    /// <summary>A registry that answers every creation with a status no route knows.</summary>
    private sealed class UnknownCreation(ITenantStore inner) : ITenantStore
    {
        public Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct) =>
            Task.FromResult(new CreateTenantOutcome((CreateTenantStatus)99, null));

        public Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct) =>
            inner.ListMembershipsAsync(user, ct);

        public Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct) =>
            inner.FindMembershipAsync(user, tenant, ct);

        public Task<IReadOnlyList<TenantId>> ListTenantIdsAsync(CancellationToken ct) => inner.ListTenantIdsAsync(ct);

        public Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct) =>
            inner.RenameAsync(tenant, actor, name, ct);

        public Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct) =>
            inner.ListMembersAsync(tenant, actor, ct);

        public Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct) =>
            inner.UpdateMemberAsync(update, ct);

        public Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct) =>
            inner.RemoveMemberAsync(removal, ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<AccountsWorld> TeamAsync() => AccountsWorld.StartAsync(_engine, descriptor: TeamTracker);

    private static Task<HttpResponseMessage> CreateAsync(AccountsWorld world, string access, string name)
        => world.AsPersonAsync(HttpMethod.Post, Tenants, access, new JsonObject { ["name"] = name });

    private static Task<HttpResponseMessage> RenameAsync(AccountsWorld world, string access, Guid tenant, string name)
        => world.AsPersonAsync(HttpMethod.Patch, $"{Tenants}/{tenant:D}", access, new JsonObject { ["name"] = name });
}
