using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Tests.Accounts;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// Minting an app token (spec §7.1, S5 plan D7, D11–D15): a person mints a personal token, an owner a service token for a
/// shared tenant — the credential answered once — never a token, never a stranger's tenant, within the live cap and the
/// creation budget, and <c>feature-disabled</c> naming the key while the descriptor leaves a kind off.
/// </summary>
/// <remarks>Ada creates <c>Acme</c> over <c>POST /tenants</c> (owner, dispatcher); Bob is seeded into it as a technician.</remarks>
public sealed class TokenRoutesTests
{
    private const string TeamTracker = "team-tracker.alvo.json";

    private const string Tokens = "/auth/v1/tokens";

    private const string Allowed = "https://app.example.test";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    private static readonly string[] _createdKeys =
        ["keyId", "token", "header", "kind", "name", "tenantId", "roles", "scopes", "expiresAt", "createdAt"];

    [Fact]
    public async Task A_person_mints_a_personal_token_and_sees_it_once()
    {
        await using var team = await TeamAsync();

        using var minted = await team.World.MintAsync(team.Ada, Personal(team, team.Tenant));
        var body = await minted.ReadJsonObjectAsync();
        var token = body["token"]!.GetValue<string>();
        using var jobs = await team.World.AsTokenAsync(HttpMethod.Get, "/api/jobs", token, tenant: team.Tenant.ToString("D"));
        using var notes = await team.World.AsTokenAsync(HttpMethod.Get, "/api/notes", token, tenant: team.Tenant.ToString("D"));

        minted.StatusCode.ShouldBe(HttpStatusCode.Created, body.ToJsonString());
        minted.Headers.CacheControl!.NoStore.ShouldBeTrue();
        minted.Headers.Location.ShouldBeNull();
        body.Select(pair => pair.Key).ShouldBe(_createdKeys);
        token.ShouldStartWith("alvo_pat_");
        token.ShouldStartWith(body["keyId"]!.GetValue<string>() + ".");
        body["header"]!.GetValue<string>().ShouldBe("X-Alvo-Api-Key");
        body["kind"]!.GetValue<string>().ShouldBe("personal");
        body["tenantId"]!.GetValue<Guid>().ShouldBe(team.Tenant);
        body["roles"]!.AsArray().ShouldBeEmpty();
        body["scopes"]!.AsArray().Select(scope => scope!.GetValue<string>()).ShouldBe(["jobs:read"]);
        body["expiresAt"]!.GetValue<DateTimeOffset>().ShouldBe(team.World.Clock.GetUtcNow().AddDays(7));
        jobs.StatusCode.ShouldBe(HttpStatusCode.OK, await jobs.ReadTextAsync());
        notes.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "notes is outside the token's scopes");
        (await notes.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.OutOfScope);
        team.World.Logs.Lines.ShouldAllBe(line => !line.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_owner_mints_a_service_token_that_acts_as_the_tenant()
    {
        await using var team = await TeamAsync();

        using var minted = await team.World.MintAsync(team.Ada, Service(team, team.Tenant, ["dispatcher"], ["jobs:read", "jobs:write"]));
        var body = await minted.ReadJsonObjectAsync();
        var token = body["token"]!.GetValue<string>();
        using var created = await team.World.AsTokenAsync(
            HttpMethod.Post, "/api/jobs", token, tenant: team.Tenant.ToString("D"), body: new { title = "Fix the sink", tenant_id = team.Tenant });

        minted.StatusCode.ShouldBe(HttpStatusCode.Created, body.ToJsonString());
        token.ShouldStartWith("alvo_svc_");
        body["kind"]!.GetValue<string>().ShouldBe("service");
        body["roles"]!.AsArray().Select(role => role!.GetValue<string>()).ShouldBe(["dispatcher"]);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var author = (await created.ReadJsonObjectAsync())["created_by"]!.GetValue<Guid>();
        author.ShouldNotBe(team.AdaId.Value);
        author.ShouldNotBe(team.BobId.Value);
        (await team.World.Store.GetAccountAsync(new UserId(author), Ct)).ShouldBeNull("a service token is no account");
    }

    [Fact]
    public async Task A_token_cannot_mint_a_token()
    {
        await using var team = await TeamAsync();
        var personal = await MintedAsync(team, Personal(team, team.Tenant));
        var service = await MintedAsync(team, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));

        foreach (var token in new[] { personal, service })
        {
            using var keyed = await team.World.AsTokenAsync(HttpMethod.Post, Tokens, token, body: Personal(team, team.Tenant));
            using var beside = await team.World.Api.SendAsync(
                HttpMethod.Post,
                Tokens,
                body: Personal(team, team.Tenant),
                headers: [new("Authorization", $"Bearer {team.Ada}"), new(team.World.Api.CredentialHeaderName, token)]);

            keyed.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await keyed.ReadTextAsync());
            (await keyed.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
            beside.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await beside.ReadTextAsync());
        }

        (await team.World.CountAsync("_api_keys")).ShouldBe(2, "the refused mints stored nothing");
    }

    [Fact]
    public async Task No_credential_is_a_bearer_challenge()
    {
        await using var team = await TeamAsync();

        using var response = await team.World.Api.SendAsync(HttpMethod.Post, Tokens, body: Personal(team, team.Tenant));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.ReadTextAsync());
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task A_member_who_is_not_an_owner_cannot_mint_a_service_token()
    {
        await using var team = await TeamAsync();

        using var response = await team.World.MintAsync(team.Bob, Service(team, team.Tenant, ["technician"], ["jobs:read"]));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.ReadTextAsync());
        (await response.ReadProblemDetailAsync()).ShouldBe("Only an owner of this tenant mints a service token for it.");
        (await team.World.CountAsync("_api_keys")).ShouldBe(0);
    }

    [Fact]
    public async Task A_stranger_s_and_an_unknown_tenant_are_the_same_404()
    {
        await using var team = await TeamAsync();
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");

        var bodies = new List<byte[]>();
        foreach (var tenant in new[] { team.Tenant, Guid.NewGuid() })
        {
            foreach (var request in new[] { Personal(team, tenant), Service(team, tenant, ["dispatcher"], ["jobs:read"]) })
            {
                using var response = await team.World.MintAsync(carol, request);
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.ReadTextAsync());
                bodies.Add(await response.Content.ReadAsByteArrayAsync(Ct));
            }
        }

        bodies.Select(Convert.ToBase64String).Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        (await team.World.CountAsync("_api_keys")).ShouldBe(0);
    }

    [Fact]
    public async Task A_personal_tenant_takes_no_service_token()
    {
        await using var team = await TeamAsync();
        var personal = (await team.World.PersonalTenantAsync(team.Ada)).Value;

        using var service = await team.World.MintAsync(team.Ada, Service(team, personal, ["dispatcher"], ["jobs:read"]));
        using var own = await team.World.MintAsync(team.Ada, Personal(team, personal));

        service.StatusCode.ShouldBe(HttpStatusCode.Conflict, await service.ReadTextAsync());
        (await service.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Conflict);
        own.StatusCode.ShouldBe(HttpStatusCode.Created, "a personal token serves the same integration");
    }

    [Fact]
    public async Task A_service_token_carries_only_roles_its_owner_holds()
    {
        await using var team = await TeamAsync();

        using var response = await team.World.MintAsync(team.Ada, Service(team, team.Tenant, ["technician"], ["jobs:read"]));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).ShouldBe([("/roles", "invalid-value")]);
        (await team.World.CountAsync("_api_keys")).ShouldBe(0);
    }

    [Fact]
    public async Task Without_access_tokens_every_route_is_404_naming_its_key()
    {
        await using var team = await TeamAsync();
        await team.World.ReapplyAsync(descriptor => descriptor["auth"]!.AsObject().Remove("accessTokens"));

        using var response = await team.World.MintAsync(team.Ada, Personal(team, team.Tenant));

        await TenantRoutesTests.FeatureDisabledAsync(response, "auth.accessTokens");
    }

    [Fact]
    public async Task A_kind_switched_off_is_404_naming_its_key()
    {
        await using var team = await TeamAsync();
        await team.World.ReapplyAsync(descriptor => descriptor["auth"]!["accessTokens"]!["service"] = false);

        using var service = await team.World.MintAsync(team.Ada, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));
        using var personal = await team.World.MintAsync(team.Ada, Personal(team, team.Tenant));
        await team.World.ReapplyAsync(descriptor =>
        {
            descriptor["auth"]!["accessTokens"]!["service"] = true;
            descriptor["auth"]!["accessTokens"]!["personal"] = false;
        });
        using var offPersonal = await team.World.MintAsync(team.Ada, Personal(team, team.Tenant));
        using var onService = await team.World.MintAsync(team.Ada, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));

        await TenantRoutesTests.FeatureDisabledAsync(service, "auth.accessTokens.service");
        personal.StatusCode.ShouldBe(HttpStatusCode.Created, await personal.ReadTextAsync());
        await TenantRoutesTests.FeatureDisabledAsync(offPersonal, "auth.accessTokens.personal");
        onService.StatusCode.ShouldBe(HttpStatusCode.Created, await onService.ReadTextAsync());
    }

    [Fact]
    public async Task Minting_answers_429_after_the_create_budget_and_shares_it_with_tenant_creation()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, api: api => api.RateLimits.Create = 2);
        var (ada, _) = await world.SignInAsync("ada");
        await TenantRoutesTests.CreatedAsync(world, ada, "Acme");
        var body = new JsonObject
        {
            ["name"] = "ci",
            ["kind"] = "personal",
            ["scopes"] = new JsonArray("jobs:read"),
            ["expiresAt"] = world.Clock.GetUtcNow().AddDays(7).ToString("O", CultureInfo.InvariantCulture),
        };

        using var second = await world.MintAsync(ada, body);
        using var third = await world.MintAsync(ada, body);

        second.StatusCode.ShouldBe(HttpStatusCode.Created, await second.ReadTextAsync());
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, await third.ReadTextAsync());
        (await third.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.RateLimited);
        int.Parse(third.Headers.GetValues("Retry-After").ShouldHaveSingleItem(), CultureInfo.InvariantCulture).ShouldBeInRange(1, 60);
        third.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await world.CountAsync("_api_keys")).ShouldBe(1);
    }

    [Fact]
    public async Task The_live_cap_is_409()
    {
        await using var team = await TeamAsync();
        await SeedLiveAsync(team, 100);

        using var response = await team.World.MintAsync(team.Ada, Personal(team, team.Tenant));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Conflict);
        (await response.ReadProblemDetailAsync()).ShouldContain("100 live tokens");
        (await team.World.CountAsync("_api_keys")).ShouldBe(100);
    }

    [Fact]
    public async Task A_non_json_body_is_415()
    {
        await using var team = await TeamAsync();

        using var response = await TenantRoutesTests.RawAsync(
            team.World, HttpMethod.Post, Tokens, team.Ada, new StringContent("kind=personal", Encoding.UTF8, "application/x-www-form-urlencoded"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.UnsupportedMediaType);
        (await team.World.CountAsync("_api_keys")).ShouldBe(0);
    }

    [Fact]
    public async Task An_invalid_body_is_422_and_stores_nothing()
    {
        await using var team = await TeamAsync();
        var body = Personal(team, team.Tenant);
        body["scopes"] = new JsonArray("*:read");

        using var response = await team.World.MintAsync(team.Ada, body);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).ShouldBe([("/scopes/0", "invalid-value")]);
        (await team.World.CountAsync("_api_keys")).ShouldBe(0);
    }

    [Fact]
    public async Task An_allowed_origin_gets_cors_headers_on_the_token_routes()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, api: api => api.Cors.Origins.Add(Allowed));

        using var refused = await world.Api.SendAsync(HttpMethod.Post, Tokens, headers: [new("Origin", Allowed)]);
        using var preflight = await world.Api.SendAsync(
            HttpMethod.Options, Tokens, headers: [new("Origin", Allowed), new("Access-Control-Request-Method", "POST")]);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refused.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
        preflight.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        preflight.Headers.GetValues("Access-Control-Allow-Methods").ShouldHaveSingleItem().ShouldContain("POST");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Personal(Team team, Guid tenant) => Body(team, "personal", tenant, null, ["jobs:read"]);

    private static JsonObject Service(Team team, Guid tenant, string[] roles, string[] scopes) => Body(team, "service", tenant, roles, scopes);

    private static JsonObject Body(Team team, string kind, Guid tenant, string[]? roles, string[] scopes)
    {
        var body = new JsonObject
        {
            ["name"] = "integration",
            ["kind"] = kind,
            ["tenantId"] = tenant.ToString("D"),
            ["scopes"] = new JsonArray([.. scopes.Select(scope => JsonValue.Create(scope))]),
            ["expiresAt"] = team.World.Clock.GetUtcNow().AddDays(7).ToString("O", CultureInfo.InvariantCulture),
        };
        if (roles is not null)
        {
            body["roles"] = new JsonArray([.. roles.Select(role => JsonValue.Create(role))]);
        }

        return body;
    }

    private static async Task<string> MintedAsync(Team team, JsonObject body)
    {
        using var response = await team.World.MintAsync(team.Ada, body);
        var minted = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, minted.ToJsonString());
        return minted["token"]!.GetValue<string>();
    }

    private static async Task SeedLiveAsync(Team team, int count)
    {
        var store = team.World.Api.Services.GetRequiredService<IAppTokenStore>();
        var now = team.World.Clock.GetUtcNow();
        for (var i = 0; i < count; i++)
        {
            var seed = new MintTokenRequest(
                $"alvo_pat_seed{i}", ApiKeyKind.Personal, "hash", "seed", team.AdaId, team.AdaId, null, [], ["jobs:read"], now.AddDays(7), count + 1, now);
            (await store.MintAsync(seed, Ct)).Status.ShouldBe(MintTokenStatus.Minted);
        }
    }

    private static async Task<Team> TeamAsync()
    {
        var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker);
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var tenant = await TenantRoutesTests.CreatedAsync(world, ada, "Acme");
        var bobId = await world.UserIdAsync(bob);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.AddMemberAsync(new TenantId(tenant), bobId, ["technician"]);
        return new Team(world, ada, bob, await world.UserIdAsync(ada), bobId, tenant);
    }

    /// <summary>A world with Ada's shared tenant and Bob in it as a technician.</summary>
    private sealed record Team(AccountsWorld World, string Ada, string Bob, UserId AdaId, UserId BobId, Guid Tenant) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => World.DisposeAsync();
    }
}
