using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tenants.SelfService;
using MMLib.Alvo.Tests.Accounts;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// An owner's invitations (spec §6.3, §6.4; S4 plan D9–D11, D13): created within <c>assignableRoles</c> with the code
/// answered once, listed with their state and never their code, revoked idempotently — by an owner only, never confirming
/// a tenant to a stranger, under one rate-limit budget, and <c>feature-disabled</c> while the descriptor leaves them off.
/// </summary>
/// <remarks>Ada creates <c>Acme</c> over <c>POST /tenants</c> (owner); Bob is seeded into it as a technician.</remarks>
public sealed class InvitationRoutesTests
{
    private const string TeamTracker = "team-tracker.alvo.json";

    private const string Allowed = "https://app.example.test";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    private static readonly string[] _createdKeys =
        ["id", "code", "roles", "isOwner", "email", "maxUses", "uses", "expiresAt", "createdBy", "createdAt", "revokedAt", "status"];

    private static readonly string[] _listedKeys =
        ["id", "roles", "isOwner", "email", "maxUses", "uses", "expiresAt", "createdBy", "createdAt", "revokedAt", "status"];

    [Fact]
    public async Task An_owner_invites_and_the_code_is_answered_once()
    {
        await using var team = await TeamAsync();

        using var created = await InviteAsync(team, team.Ada, team.Tenant, Technician());
        var invitation = await created.ReadJsonObjectAsync();
        using var listed = await ListAsync(team, team.Ada, team.Tenant);
        var listedText = await listed.ReadTextAsync();

        created.StatusCode.ShouldBe(HttpStatusCode.Created, invitation.ToJsonString());
        created.Headers.CacheControl!.NoStore.ShouldBeTrue();
        created.Headers.Location.ShouldBeNull("there is no single-invitation GET (D11)");
        invitation.Select(pair => pair.Key).ShouldBe(_createdKeys);
        var code = invitation["code"]!.GetValue<string>();
        code.ShouldStartWith(InvitationCode.Prefix);
        code.Length.ShouldBe(31);
        invitation["status"]!.GetValue<string>().ShouldBe("active");
        invitation["uses"]!.GetValue<int>().ShouldBe(0);
        invitation["createdBy"]!.GetValue<Guid>().ShouldBe(team.AdaId.Value);
        invitation["expiresAt"]!.GetValue<DateTimeOffset>().ShouldBe(team.World.Clock.GetUtcNow().AddDays(7));
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, listedText);
        var item = (await listed.ReadJsonArrayAsync()).ShouldHaveSingleItem();
        item.Select(pair => pair.Key).ShouldBe(_listedKeys);
        item["id"]!.GetValue<Guid>().ShouldBe(invitation["id"]!.GetValue<Guid>());
        listedText.ShouldNotContain(code);
        team.World.Logs.Lines.ShouldAllBe(line => !line.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_member_who_is_not_an_owner_cannot_invite_list_or_revoke()
    {
        await using var team = await TeamAsync();
        var invitation = await InvitedAsync(team, team.Ada, team.Tenant, Technician());

        using var invite = await InviteAsync(team, team.Bob, team.Tenant, Technician());
        using var list = await ListAsync(team, team.Bob, team.Tenant);
        using var revoke = await RevokeAsync(team, team.Bob, team.Tenant, invitation);

        foreach (var refused in new[] { invite, list, revoke })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
            (await refused.ReadProblemDetailAsync()).ShouldBe("Only an owner of this tenant makes this change.");
        }

        (await ListedAsync(team, team.Ada, team.Tenant)).ShouldHaveSingleItem()["status"]!.GetValue<string>().ShouldBe("active");
    }

    [Fact]
    public async Task A_non_member_and_an_unknown_tenant_get_the_same_404()
    {
        await using var team = await TeamAsync();
        var invitation = await InvitedAsync(team, team.Ada, team.Tenant, Technician());
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");

        var bodies = new List<byte[]>();
        foreach (var tenant in new[] { team.Tenant.Value, Guid.NewGuid() })
        {
            foreach (var request in Requests(tenant, invitation))
            {
                using var response = await team.World.AsPersonAsync(request.Method, request.Path, carol, request.Body);
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{request.Method} {request.Path}: {await response.ReadTextAsync()}");
                bodies.Add(await response.Content.ReadAsByteArrayAsync(Ct));
            }
        }

        bodies.Select(Convert.ToBase64String).Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        (await ListedAsync(team, team.Ada, team.Tenant)).ShouldHaveSingleItem()["status"]!.GetValue<string>().ShouldBe("active");
    }

    [Fact]
    public async Task A_personal_tenant_takes_no_invitation()
    {
        await using var team = await TeamAsync();
        var personal = (await team.World.PersonalTenantAsync(team.Ada)).Value;

        using var invite = await InviteAsync(team, team.Ada, personal, Technician());
        using var list = await ListAsync(team, team.Ada, personal);

        foreach (var refused in new[] { invite, list })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.ReadTextAsync());
            (await refused.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Conflict);
        }
    }

    [Fact]
    public async Task The_list_shows_each_state()
    {
        await using var team = await TeamAsync();
        var clock = team.World.Clock;
        var expiring = await InvitedAsync(team, team.Ada, team.Tenant, Technician(expiresAt: clock.GetUtcNow().AddDays(1)));
        var revoked = await InvitedAsync(team, team.Ada, team.Tenant, Technician());
        var usedUp = await InvitedCodeAsync(team, Technician());
        var active = await InvitedAsync(team, team.Ada, team.Tenant, Technician());
        using var revoking = await RevokeAsync(team, team.Ada, team.Tenant, revoked);
        revoking.StatusCode.ShouldBe(HttpStatusCode.NoContent, await revoking.ReadTextAsync());
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");
        var accepted = await team.World.Tenants.AcceptInvitationAsync(
            new AcceptInvitationRequest(InvitationCode.Hash(usedUp.Code), await team.World.UserIdAsync(carol), clock.GetUtcNow()), Ct);
        accepted.Status.ShouldBe(AcceptInvitationStatus.Joined);
        clock.Advance(TimeSpan.FromDays(2));
        var (ada, _) = await team.World.SignInAsync("ada");

        var listed = await ListedAsync(team, ada, team.Tenant);

        listed.Select(item => item["id"]!.GetValue<Guid>()).ShouldBe([active, usedUp.Id, revoked, expiring], "newest first");
        listed.Select(item => item["status"]!.GetValue<string>()).ShouldBe(["active", "usedUp", "revoked", "expired"]);
        listed[1]["uses"]!.GetValue<int>().ShouldBe(1);
        listed[2]["revokedAt"].ShouldNotBeNull();
        listed[0]["revokedAt"].ShouldBeNull();
    }

    [Fact]
    public async Task Revoking_is_idempotent_and_another_tenant_s_invitation_is_404()
    {
        await using var team = await TeamAsync();
        var invitation = await InvitedAsync(team, team.Ada, team.Tenant, Technician());
        var beta = await TenantRoutesTests.CreatedAsync(team.World, team.Ada, "Beta");
        var betas = await InvitedAsync(team, team.Ada, beta, Technician());

        using var first = await RevokeAsync(team, team.Ada, team.Tenant, invitation);
        var firstInstant = (await ListedAsync(team, team.Ada, team.Tenant)).Single()["revokedAt"]!.GetValue<DateTimeOffset>();
        team.World.Clock.Advance(TimeSpan.FromMinutes(5));
        using var again = await RevokeAsync(team, team.Ada, team.Tenant, invitation);
        using var foreign = await RevokeAsync(team, team.Ada, team.Tenant, betas);
        using var unknown = await RevokeAsync(team, team.Ada, team.Tenant, Guid.NewGuid());

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent, await first.ReadTextAsync());
        first.Headers.CacheControl!.NoStore.ShouldBeTrue();
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent, await again.ReadTextAsync());
        (await ListedAsync(team, team.Ada, team.Tenant)).Single()["revokedAt"]!.GetValue<DateTimeOffset>().ShouldBe(firstInstant);
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, await foreign.ReadTextAsync());
        (await foreign.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(await unknown.Content.ReadAsByteArrayAsync(Ct));
        (await ListedAsync(team, team.Ada, beta)).Single()["status"]!.GetValue<string>().ShouldBe("active");
    }

    [Fact]
    public async Task Without_invitations_every_route_is_404_naming_its_key()
    {
        await using var team = await TeamAsync();
        await team.World.ReapplyAsync(descriptor => descriptor["tenancy"]!["selfService"]!.AsObject().Remove("invitations"));

        foreach (var request in Requests(team.Tenant.Value, Guid.NewGuid()))
        {
            using var response = await team.World.AsPersonAsync(request.Method, request.Path, team.Ada, request.Body);
            await TenantRoutesTests.FeatureDisabledAsync(response, SelfServiceProblems.InvitationsKey);
        }

        await team.World.ReapplyAsync(descriptor => descriptor["tenancy"]!.AsObject().Remove("selfService"));

        foreach (var request in Requests(team.Tenant.Value, Guid.NewGuid()))
        {
            using var response = await team.World.AsPersonAsync(request.Method, request.Path, team.Ada, request.Body);
            await TenantRoutesTests.FeatureDisabledAsync(response, SelfServiceProblems.SelfServiceKey);
        }
    }

    [Fact]
    public async Task An_api_key_is_403_and_no_credential_is_a_bearer_challenge()
    {
        var integration = new TestApiKey("integration", ["authenticated"], ["*:read", "*:write"]);
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, keys: [integration]);

        foreach (var request in Requests(Guid.NewGuid(), Guid.NewGuid()))
        {
            using var keyed = await world.Api.SendAsync(request.Method, request.Path, integration, body: request.Body);
            using var anonymous = await world.Api.SendAsync(request.Method, request.Path, body: request.Body);

            keyed.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await keyed.ReadTextAsync());
            (await keyed.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await anonymous.ReadTextAsync());
            anonymous.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
            anonymous.Headers.CacheControl!.NoStore.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Invitation_routes_answer_429_after_their_limit()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, api: api => api.RateLimits.Invitations = 2);
        var (ada, _) = await world.SignInAsync("ada");
        var tenant = await TenantRoutesTests.CreatedAsync(world, ada, "Acme");
        var path = $"{TenantRoutesTests.Tenants}/{tenant:D}/invitations";

        using var first = await world.AsPersonAsync(HttpMethod.Post, path, ada, Technician());
        using var second = await world.AsPersonAsync(HttpMethod.Get, path, ada);
        using var third = await world.AsPersonAsync(HttpMethod.Delete, $"{path}/{Guid.NewGuid():D}", ada);

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.ReadTextAsync());
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.ReadTextAsync());
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, await third.ReadTextAsync());
        (await third.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.RateLimited);
        int.Parse(third.Headers.GetValues("Retry-After").ShouldHaveSingleItem(), CultureInfo.InvariantCulture).ShouldBeInRange(1, 60);
        third.Headers.CacheControl!.NoStore.ShouldBeTrue();
        for (var i = 0; i < 3; i++)
        {
            using var data = await world.AsPersonAsync(HttpMethod.Get, "/api/regions", ada);
            data.StatusCode.ShouldBe(HttpStatusCode.OK, await data.ReadTextAsync());
            using var tenants = await world.AsPersonAsync(HttpMethod.Get, TenantRoutesTests.Tenants, ada);
            tenants.StatusCode.ShouldBe(HttpStatusCode.OK, "only the invitation routes are charged");
        }
    }

    [Fact]
    public async Task A_non_json_body_is_415()
    {
        await using var team = await TeamAsync();

        using var response = await TenantRoutesTests.RawAsync(
            team.World,
            HttpMethod.Post,
            Path(team.Tenant.Value),
            team.Ada,
            new StringContent("roles=technician", Encoding.UTF8, "application/x-www-form-urlencoded"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.UnsupportedMediaType);
        (await ListedAsync(team, team.Ada, team.Tenant)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_role_outside_assignable_roles_is_refused_and_nothing_is_created()
    {
        await using var team = await TeamAsync();

        using var response = await InviteAsync(team, team.Ada, team.Tenant, new JsonObject { ["roles"] = new JsonArray("owner") });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).ShouldBe([("/roles/0", "enum-value")]);
        (await ListedAsync(team, team.Ada, team.Tenant)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_allowed_origin_gets_cors_headers_on_the_invitation_routes()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, api: api => api.Cors.Origins.Add(Allowed));
        var (ada, _) = await world.SignInAsync("ada");
        var path = Path(await TenantRoutesTests.CreatedAsync(world, ada, "Acme"));

        using var listed = await world.Api.SendAsync(HttpMethod.Get, path, headers: [new("Origin", Allowed), new("Authorization", $"Bearer {ada}")]);
        using var refused = await world.Api.SendAsync(HttpMethod.Get, path, headers: [new("Origin", Allowed)]);
        using var preflight = await world.Api.SendAsync(
            HttpMethod.Options, path, headers: [new("Origin", Allowed), new("Access-Control-Request-Method", "POST")]);

        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.ReadTextAsync());
        listed.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refused.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
        preflight.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        preflight.Headers.GetValues("Access-Control-Allow-Methods").ShouldHaveSingleItem().ShouldContain("POST");
    }

    /// <summary>A body inviting a technician, expiring at <paramref name="expiresAt"/> if given.</summary>
    internal static JsonObject Technician(DateTimeOffset? expiresAt = null)
    {
        var body = new JsonObject { ["roles"] = new JsonArray("technician") };
        if (expiresAt is { } instant)
        {
            body["expiresAt"] = instant.ToString("O", CultureInfo.InvariantCulture);
        }

        return body;
    }

    /// <summary>The invitations path of <paramref name="tenant"/>.</summary>
    internal static string Path(Guid tenant) => $"{TenantRoutesTests.Tenants}/{tenant:D}/invitations";

    /// <summary>Creates an invitation as <paramref name="access"/>'s person, asserting it was created.</summary>
    internal static async Task<(Guid Id, string Code)> InvitedAsync(AccountsWorld world, string access, Guid tenant, JsonObject body)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Post, Path(tenant), access, body);
        var created = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, created.ToJsonString());
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        return (created["id"]!.GetValue<Guid>(), created["code"]!.GetValue<string>());
    }

    private static IEnumerable<(HttpMethod Method, string Path, JsonObject? Body)> Requests(Guid tenant, Guid invitation) =>
    [
        (HttpMethod.Post, Path(tenant), Technician()),
        (HttpMethod.Get, Path(tenant), null),
        (HttpMethod.Delete, $"{Path(tenant)}/{invitation:D}", null),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Team> TeamAsync()
    {
        var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker);
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var tenant = new TenantId(await TenantRoutesTests.CreatedAsync(world, ada, "Acme"));
        var bobId = await world.UserIdAsync(bob);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.AddMemberAsync(tenant, bobId, ["technician"]);
        return new Team(world, ada, bob, await world.UserIdAsync(ada), tenant);
    }

    private static Task<HttpResponseMessage> InviteAsync(Team team, string access, TenantId tenant, JsonObject body)
        => InviteAsync(team, access, tenant.Value, body);

    private static Task<HttpResponseMessage> InviteAsync(Team team, string access, Guid tenant, JsonObject body)
        => team.World.AsPersonAsync(HttpMethod.Post, Path(tenant), access, body);

    private static Task<HttpResponseMessage> ListAsync(Team team, string access, TenantId tenant) => ListAsync(team, access, tenant.Value);

    private static Task<HttpResponseMessage> ListAsync(Team team, string access, Guid tenant)
        => team.World.AsPersonAsync(HttpMethod.Get, Path(tenant), access);

    private static Task<HttpResponseMessage> RevokeAsync(Team team, string access, TenantId tenant, Guid invitation)
        => team.World.AsPersonAsync(HttpMethod.Delete, $"{Path(tenant.Value)}/{invitation:D}", access);

    private static async Task<Guid> InvitedAsync(Team team, string access, TenantId tenant, JsonObject body)
        => (await InvitedAsync(team.World, access, tenant.Value, body)).Id;

    private static async Task<Guid> InvitedAsync(Team team, string access, Guid tenant, JsonObject body)
        => (await InvitedAsync(team.World, access, tenant, body)).Id;

    private static Task<(Guid Id, string Code)> InvitedCodeAsync(Team team, JsonObject body)
        => InvitedAsync(team.World, team.Ada, team.Tenant.Value, body);

    private static Task<IReadOnlyList<JsonObject>> ListedAsync(Team team, string access, TenantId tenant)
        => ListedAsync(team, access, tenant.Value);

    private static async Task<IReadOnlyList<JsonObject>> ListedAsync(Team team, string access, Guid tenant)
    {
        using var response = await ListAsync(team, access, tenant);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return await response.ReadJsonArrayAsync();
    }

    /// <summary>A world with Ada's shared tenant and Bob in it as a technician.</summary>
    private sealed record Team(AccountsWorld World, string Ada, string Bob, UserId AdaId, TenantId Tenant) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => World.DisposeAsync();
    }
}
