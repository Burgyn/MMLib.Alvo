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

    private const string AppTokenAsBearerDetail =
        "Personal and service tokens are not bearer tokens. Present the token in the X-Alvo-Api-Key header, "
        + "not as 'Authorization: Bearer'.";

    private const string PersonOnlyDetail =
        "This route answers a signed-in person only. Present the access token sign-in issued as 'Authorization: Bearer "
        + "<token>'; an API key, a personal token or a service token does not reach it.";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    private static readonly string[] _listedKeys =
    [
        "keyId", "kind", "name", "tenantId", "roles", "scopes", "createdBy", "createdAt", "expiresAt", "lastUsedAt", "revokedAt", "status",
    ];

    private static readonly string[] _createdKeys =
    [
        "keyId", "token", "header", "kind", "name", "tenantId", "roles", "scopes", "createdBy", "createdAt", "expiresAt", "lastUsedAt",
        "revokedAt", "status",
    ];

    private static readonly (HttpMethod Method, string Path)[] _bearerSurfaces =
    [
        (HttpMethod.Get, "/api/jobs"),
        (HttpMethod.Get, "/auth/v1/user"),
        (HttpMethod.Post, "/auth/v1/logout"),
        (HttpMethod.Get, "/auth/v1/tenants"),
        (HttpMethod.Get, Tokens),
    ];

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
        body["createdBy"]!["id"]!.GetValue<Guid>().ShouldBe(team.AdaId.Value, "the minter, as the list will name them (#542)");
        body["createdBy"]!["name"]!.GetValue<string>().ShouldBe(await DisplayNameAsync(team, team.Ada));
        body["status"]!.GetValue<string>().ShouldBe("active");
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

            await PersonOnlyChallengeAsync(keyed);
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
    public async Task A_personal_tenant_the_descriptor_no_longer_honours_is_the_stranger_404_for_a_service_token()
    {
        await using var team = await TeamAsync();
        var personal = (await team.World.PersonalTenantAsync(team.Ada)).Value;
        await team.World.ReapplyAsync(descriptor => descriptor["tenancy"]!["selfService"]!.AsObject().Remove("personalTenant"));

        using var unhonoured = await team.World.MintAsync(team.Ada, Service(team, personal, ["dispatcher"], ["jobs:read"]));
        using var unknown = await team.World.MintAsync(team.Ada, Service(team, Guid.NewGuid(), ["dispatcher"], ["jobs:read"]));

        unhonoured.StatusCode.ShouldBe(HttpStatusCode.NotFound, await unhonoured.ReadTextAsync());
        (await unhonoured.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(
            await unknown.Content.ReadAsByteArrayAsync(Ct),
            "a membership no route honours is a stranger's, not the personal tenant's 409");
        (await team.World.CountAsync("_api_keys")).ShouldBe(0);
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
        await SeedLiveAsync(team, team.AdaId, 100);

        using var response = await team.World.MintAsync(team.Ada, Personal(team, team.Tenant));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Conflict);
        (await response.ReadProblemDetailAsync()).ShouldContain("100 live tokens");
        (await team.World.CountAsync("_api_keys")).ShouldBe(100);
    }

    [Fact]
    public async Task A_stranger_at_the_live_cap_is_the_same_404_for_either_kind()
    {
        await using var team = await TeamAsync();
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");
        await SeedLiveAsync(team, await team.World.UserIdAsync(carol), 100);

        var bodies = new List<byte[]>();
        foreach (var request in new[] { Personal(team, team.Tenant), Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]) })
        {
            using var response = await team.World.MintAsync(carol, request);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.ReadTextAsync());
            bodies.Add(await response.Content.ReadAsByteArrayAsync(Ct));
        }

        bodies.Select(Convert.ToBase64String).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(1, "the cap is the stranger's own, and must not tell a service mint from a personal one");
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

    [Fact]
    public async Task The_list_shows_my_tokens_and_my_tenants_service_tokens_never_a_secret()
    {
        await using var team = await TeamAsync(bobOwns: true);
        var personal = await MintedAsync(team, Personal(team, team.Tenant));
        team.World.Clock.Advance(TimeSpan.FromMinutes(1));
        using var bobs = await team.World.MintAsync(team.Bob, Personal(team, team.Tenant));
        team.World.Clock.Advance(TimeSpan.FromMinutes(1));
        var service = await MintedAsync(team, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));
        team.World.Clock.Advance(TimeSpan.FromMinutes(1));
        var newest = await MintedAsync(team, Personal(team, team.Tenant));

        var (ada, adaStatus) = await ListAsync(team, team.Ada);
        var (bob, _) = await ListAsync(team, team.Bob);

        adaStatus.ShouldBe(HttpStatusCode.OK);
        ada.Select(item => item!["keyId"]!.GetValue<string>()).ShouldBe([KeyOf(newest), KeyOf(service), KeyOf(personal)]);
        bob.Count.ShouldBe(2, "Bob's own personal token and Ada's service token, never Ada's personal ones");
        bob.Select(item => item!["kind"]!.GetValue<string>()).Order().ShouldBe(["personal", "service"]);
        var first = ada[0]!.AsObject();
        first.Select(pair => pair.Key).ShouldBe(_listedKeys);
        first["createdBy"]!.AsObject().Select(pair => pair.Key).ShouldBe(["id", "name"]);
        first["createdBy"]!["id"]!.GetValue<Guid>().ShouldBe(team.AdaId.Value);
        first["createdBy"]!["name"]!.GetValue<string>().ShouldBe(await DisplayNameAsync(team, team.Ada));
        first["lastUsedAt"].ShouldBeNull();
        first["revokedAt"].ShouldBeNull();
        var text = new JsonArray([.. ada.Select(item => item!.DeepClone())]).ToJsonString();
        text.ShouldNotContain("\"token\"");
        text.ShouldNotContain(newest, Case.Sensitive);
        text.ShouldNotContain(newest[(newest.IndexOf('.', StringComparison.Ordinal) + 1)..], Case.Sensitive);
    }

    [Fact]
    public async Task A_plain_member_sees_none_of_the_service_tokens()
    {
        await using var team = await TeamAsync();
        await MintedAsync(team, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));

        var (bob, _) = await ListAsync(team, team.Bob);

        bob.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_list_shows_each_state()
    {
        await using var team = await TeamAsync();
        var active = await MintedAsync(team, Personal(team, team.Tenant));
        var expiring = Personal(team, team.Tenant);
        expiring["expiresAt"] = team.World.Clock.GetUtcNow().AddHours(1).ToString("O", CultureInfo.InvariantCulture);
        var expired = await MintedAsync(team, expiring);
        var revoked = await MintedAsync(team, Personal(team, team.Tenant));
        (await RevokeAsync(team, team.Ada, KeyOf(revoked))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        team.World.Clock.Advance(TimeSpan.FromHours(2));
        var (later, _) = await team.World.SignInAsync("ada");

        var (list, _) = await ListAsync(team, later);

        var statuses = StatusesOf(list);
        statuses[KeyOf(active)].ShouldBe("active");
        statuses[KeyOf(expired)].ShouldBe("expired");
        statuses[KeyOf(revoked)].ShouldBe("revoked");
        statuses.Count.ShouldBe(3);
        list.Single(item => item!["keyId"]!.GetValue<string>() == KeyOf(revoked))!["revokedAt"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Last_used_is_recorded_within_a_minute()
    {
        await using var team = await TeamAsync();
        var token = await MintedAsync(team, Personal(team, team.Tenant));
        using var first = await team.World.AsTokenAsync(HttpMethod.Get, "/api/jobs", token, tenant: team.Tenant.ToString("D"));
        var (afterFirst, _) = await ListAsync(team, team.Ada);
        var stamped = afterFirst.Single()!["lastUsedAt"]!.GetValue<DateTimeOffset>();
        team.World.Clock.Advance(TimeSpan.FromSeconds(10));
        using var second = await team.World.AsTokenAsync(HttpMethod.Get, "/api/jobs", token, tenant: team.Tenant.ToString("D"));

        var (afterSecond, _) = await ListAsync(team, team.Ada);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.ReadTextAsync());
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.ReadTextAsync());
        stamped.ShouldBe(team.World.Clock.GetUtcNow().AddSeconds(-10));
        afterSecond.Single()!["lastUsedAt"]!.GetValue<DateTimeOffset>().ShouldBe(stamped);
    }

    [Fact]
    public async Task Revoking_kills_the_token_on_its_next_request()
    {
        await using var team = await TeamAsync();
        var token = await MintedAsync(team, Personal(team, team.Tenant));
        using var before = await team.World.AsTokenAsync(HttpMethod.Get, "/api/jobs", token, tenant: team.Tenant.ToString("D"));

        using var revoked = await RevokeAsync(team, team.Ada, KeyOf(token));
        using var after = await team.World.AsTokenAsync(HttpMethod.Get, "/api/jobs", token, tenant: team.Tenant.ToString("D"));

        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, await revoked.ReadTextAsync());
        revoked.Headers.CacheControl!.NoStore.ShouldBeTrue();
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await after.ReadTextAsync());
    }

    [Fact]
    public async Task Revoking_twice_is_204_and_keeps_the_first_instant()
    {
        await using var team = await TeamAsync();
        var token = await MintedAsync(team, Personal(team, team.Tenant));
        using var once = await RevokeAsync(team, team.Ada, KeyOf(token));
        var (listed, _) = await ListAsync(team, team.Ada);
        var stamped = listed.Single()!["revokedAt"]!.GetValue<DateTimeOffset>();
        team.World.Clock.Advance(TimeSpan.FromMinutes(5));

        using var twice = await RevokeAsync(team, team.Ada, KeyOf(token));

        once.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        twice.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(team, team.Ada)).Items.Single()!["revokedAt"]!.GetValue<DateTimeOffset>().ShouldBe(stamped);
    }

    [Fact]
    public async Task Any_owner_revokes_a_service_token_and_nobody_else_s_personal_one()
    {
        await using var team = await TeamAsync(bobOwns: true);
        var service = await MintedAsync(team, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));
        var personal = await MintedAsync(team, Personal(team, team.Tenant));

        using var other = await RevokeAsync(team, team.Bob, KeyOf(personal));
        using var unknown = await RevokeAsync(team, team.Bob, "alvo_pat_nosuchkey");
        using var owner = await RevokeAsync(team, team.Bob, KeyOf(service));

        owner.StatusCode.ShouldBe(HttpStatusCode.NoContent, await owner.ReadTextAsync());
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound, await other.ReadTextAsync());
        (await other.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(await unknown.Content.ReadAsByteArrayAsync(Ct));
        using var usable = await team.World.AsTokenAsync(HttpMethod.Get, "/api/jobs", personal, tenant: team.Tenant.ToString("D"));
        usable.StatusCode.ShouldBe(HttpStatusCode.OK, "the personal token is untouched");
    }

    [Fact]
    public async Task A_member_s_list_after_self_service_is_switched_off_shows_only_personal_tokens()
    {
        await using var team = await TeamAsync();
        await MintedAsync(team, Personal(team, team.Tenant));
        await MintedAsync(team, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));
        await team.World.ReapplyAsync(AccountsWorld.WithoutSelfService);

        var (list, status) = await ListAsync(team, team.Ada);

        status.ShouldBe(HttpStatusCode.OK);
        list.Select(item => item!["kind"]!.GetValue<string>()).ShouldBe(["personal"]);
    }

    [Fact]
    public async Task A_service_token_the_descriptor_no_longer_honours_cannot_be_revoked()
    {
        await using var team = await TeamAsync();
        var service = await MintedAsync(team, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));
        JsonNode? selfService = null;
        await team.World.ReapplyAsync(descriptor =>
        {
            selfService = descriptor["tenancy"]!["selfService"]!.DeepClone();
            AccountsWorld.WithoutSelfService(descriptor);
        });
        using var unknown = await RevokeAsync(team, team.Ada, "alvo_svc_nosuchkey");

        using var refused = await RevokeAsync(team, team.Ada, KeyOf(service));
        await team.World.ReapplyAsync(descriptor =>
        {
            descriptor["tenancy"]!["selfService"] = selfService;
            descriptor["auth"]!["accessTokens"]!["service"] = true;
        });
        var (list, _) = await ListAsync(team, team.Ada);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, await refused.ReadTextAsync());
        (await refused.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(await unknown.Content.ReadAsByteArrayAsync(Ct));
        StatusesOf(list)[KeyOf(service)].ShouldBe("active", "the refused revoke wrote nothing");
    }

    [Fact]
    public async Task A_plain_member_revoking_a_service_token_is_the_same_404_as_an_unknown_id()
    {
        await using var team = await TeamAsync();
        var service = await MintedAsync(team, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));

        using var plain = await RevokeAsync(team, team.Bob, KeyOf(service));
        using var unknown = await RevokeAsync(team, team.Bob, "alvo_svc_nosuchkey");

        plain.StatusCode.ShouldBe(HttpStatusCode.NotFound, await plain.ReadTextAsync());
        (await plain.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(await unknown.Content.ReadAsByteArrayAsync(Ct));
        (await ListAsync(team, team.Ada)).Items.Single()["status"]!.GetValue<string>().ShouldBe("active");
    }

    [Fact]
    public async Task Without_access_tokens_list_and_revoke_are_404_naming_its_key()
    {
        await using var team = await TeamAsync();
        await team.World.ReapplyAsync(descriptor => descriptor["auth"]!.AsObject().Remove("accessTokens"));

        using var list = await team.World.AsPersonAsync(HttpMethod.Get, Tokens, team.Ada);
        using var revoke = await RevokeAsync(team, team.Ada, "alvo_pat_whatever");

        await TenantRoutesTests.FeatureDisabledAsync(list, "auth.accessTokens");
        await TenantRoutesTests.FeatureDisabledAsync(revoke, "auth.accessTokens");
    }

    [Fact]
    public async Task A_token_cannot_list_or_revoke()
    {
        await using var team = await TeamAsync();
        var token = await MintedAsync(team, Personal(team, team.Tenant));

        using var list = await team.World.AsTokenAsync(HttpMethod.Get, Tokens, token);
        using var revoke = await team.World.AsTokenAsync(HttpMethod.Delete, $"{Tokens}/{KeyOf(token)}", token);

        await PersonOnlyChallengeAsync(list);
        await PersonOnlyChallengeAsync(revoke);
        using var still = await team.World.AsTokenAsync(HttpMethod.Get, "/api/jobs", token, tenant: team.Tenant.ToString("D"));
        still.StatusCode.ShouldBe(HttpStatusCode.OK, "a refused revoke revoked nothing");
    }

    /// <summary>
    /// #539: an app token sent as <c>Authorization: Bearer</c> gets one 401 on every surface that reads a Bearer — the Data
    /// API, the signed-in person's own routes and the self-service routes — with a fixed detail naming the header it
    /// belongs in. Live, revoked and made-up tokens of either kind answer alike, so the answer is no oracle; none is
    /// authenticated (its <c>lastUsedAt</c> stays empty) and the same live token still works in the header named.
    /// </summary>
    [Fact]
    public async Task An_app_token_as_a_bearer_is_one_401_naming_the_api_key_header_on_every_surface()
    {
        await using var team = await TeamAsync();
        var personal = await MintedAsync(team, Personal(team, team.Tenant));
        var service = await MintedAsync(team, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));
        var revoked = await MintedAsync(team, Personal(team, team.Tenant));
        (await RevokeAsync(team, team.Ada, KeyOf(revoked))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        string[] tokens = [personal, service, revoked, "alvo_svc_nosuchkey.nosecret"];

        var answers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (method, path) in _bearerSurfaces)
        {
            foreach (var token in tokens)
            {
                answers.Add(await AppTokenAsBearerAnswerAsync(team, method, path, token));
            }
        }

        answers.ShouldHaveSingleItem().ShouldBe(
            $"401 unauthenticated Bearer error=\"invalid_token\" {AppTokenAsBearerDetail}", "one answer for every token on every surface");
        (await ListAsync(team, team.Ada)).Items.ShouldAllBe(item => item["lastUsedAt"] == null, "a Bearer never authenticated a token");
        using var asKey = await team.World.AsTokenAsync(HttpMethod.Get, "/api/jobs", service, tenant: team.Tenant.ToString("D"));
        asKey.StatusCode.ShouldBe(HttpStatusCode.OK, await asKey.ReadTextAsync());
    }

    /// <summary>
    /// #542 item 2, the mirror of #539: every route only a signed-in person reaches answers an API key of any kind —
    /// configured, personal, service, revoked or made up — with the one 401 whose bare challenge names Bearer, the route's
    /// only scheme (RFC 6750 §3.1). None is resolved, so none is told it is insufficient, and none writes anything.
    /// </summary>
    [Fact]
    public async Task An_api_key_of_any_kind_is_one_bare_bearer_challenge_on_every_person_only_route()
    {
        await using var team = await TeamAsync(api: Unthrottled);
        var personal = await MintedAsync(team, Personal(team, team.Tenant));
        var service = await MintedAsync(team, Service(team, team.Tenant, ["dispatcher"], ["jobs:read"]));
        var revoked = await MintedAsync(team, Personal(team, team.Tenant));
        (await RevokeAsync(team, team.Ada, KeyOf(revoked))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        string[] keys = [AccountsWorld.Operator.Presented, personal, service, revoked, "alvo_svc_nosuchkey.nosecret", "made-up"];

        var answers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (method, path) in PersonOnlyRoutes(team, KeyOf(service)))
        {
            foreach (var key in keys)
            {
                answers.Add(await ApiKeyAnswerAsync(team, method, path, key));
            }
        }

        answers.ShouldHaveSingleItem().ShouldBe($"401 unauthenticated Bearer {PersonOnlyDetail}", "one answer for every key on every route");
        (await team.World.CountAsync("_api_keys")).ShouldBe(3, "no refused request minted anything");
        (await ListAsync(team, team.Ada)).Items.ShouldAllBe(item => item["lastUsedAt"] == null, "no key was ever resolved");
    }

    private static async Task<string> AppTokenAsBearerAnswerAsync(Team team, HttpMethod method, string path, string token)
    {
        using var response = await team.World.AsPersonAsync(method, path, token, tenant: team.Tenant.ToString("D"));
        var text = await response.ReadTextAsync();
        text.ShouldNotContain("alvo_", Case.Sensitive, $"{method} {path} echoes no part of the token, not even its prefix");
        return $"{(int)response.StatusCode} {await response.ReadProblemTypeAsync()} {response.Headers.WwwAuthenticate} "
            + await response.ReadProblemDetailAsync();
    }

    private static async Task<string> ApiKeyAnswerAsync(Team team, HttpMethod method, string path, string key)
    {
        using var response = await team.World.AsTokenAsync(method, path, key, body: new { });
        var text = await response.ReadTextAsync();
        text.ShouldNotContain(key, Case.Sensitive, $"{method} {path} echoes no part of the key");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue($"{method} {path} is about a person");
        return $"{(int)response.StatusCode} {await response.ReadProblemTypeAsync()} {response.Headers.WwwAuthenticate} "
            + await response.ReadProblemDetailAsync();
    }

    private static async Task PersonOnlyChallengeAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.ReadTextAsync());
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Unauthenticated);
        (await response.ReadProblemDetailAsync()).ShouldBe(PersonOnlyDetail);
    }

    private static (HttpMethod Method, string Path)[] PersonOnlyRoutes(Team team, string keyId)
    {
        var tenant = $"/auth/v1/tenants/{team.Tenant:D}";
        var bob = $"{tenant}/members/{team.BobId.Value:D}";
        return
        [
            (HttpMethod.Get, "/auth/v1/user"),
            (HttpMethod.Patch, "/auth/v1/user"),
            (HttpMethod.Post, "/auth/v1/logout"),
            (HttpMethod.Get, "/auth/v1/tenants"),
            (HttpMethod.Post, "/auth/v1/tenants"),
            (HttpMethod.Patch, tenant),
            (HttpMethod.Get, $"{tenant}/members"),
            (HttpMethod.Patch, bob),
            (HttpMethod.Delete, bob),
            (HttpMethod.Post, $"{tenant}/invitations"),
            (HttpMethod.Get, $"{tenant}/invitations"),
            (HttpMethod.Delete, $"{tenant}/invitations/{Guid.NewGuid():D}"),
            (HttpMethod.Post, "/auth/v1/invitations/accept"),
            (HttpMethod.Post, Tokens),
            (HttpMethod.Get, Tokens),
            (HttpMethod.Delete, $"{Tokens}/{keyId}"),
        ];
    }

    private static void Unthrottled(AlvoApiOptions options) => options.RateLimits.Create = 1000;

    private static async Task<string> DisplayNameAsync(Team team, string access)
    {
        using var user = await team.World.AsPersonAsync(HttpMethod.Get, "/auth/v1/user", access);
        return (await user.ReadJsonObjectAsync())["name"]!.GetValue<string>();
    }

    private static string KeyOf(string token) => token[..token.IndexOf('.', StringComparison.Ordinal)];

    private static Dictionary<string, string> StatusesOf(IReadOnlyList<JsonObject> list) =>
        list.ToDictionary(item => item!["keyId"]!.GetValue<string>(), item => item!["status"]!.GetValue<string>());

    private static async Task<(IReadOnlyList<JsonObject> Items, HttpStatusCode Status)> ListAsync(Team team, string access)
    {
        using var response = await team.World.AsPersonAsync(HttpMethod.Get, Tokens, access);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        return (response.IsSuccessStatusCode ? await response.ReadJsonArrayAsync() : [], response.StatusCode);
    }

    private static Task<HttpResponseMessage> RevokeAsync(Team team, string access, string keyId) =>
        team.World.AsPersonAsync(HttpMethod.Delete, $"{Tokens}/{keyId}", access);

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

    private static async Task SeedLiveAsync(Team team, UserId minter, int count)
    {
        var store = team.World.Api.Services.GetRequiredService<IAppTokenStore>();
        var now = team.World.Clock.GetUtcNow();
        for (var i = 0; i < count; i++)
        {
            var seed = new MintTokenRequest(
                $"alvo_pat_seed{i}", ApiKeyKind.Personal, "hash", "seed", minter, minter, null, [], ["jobs:read"], now.AddDays(7), count + 1, now);
            (await store.MintAsync(seed, Ct)).Status.ShouldBe(MintTokenStatus.Minted);
        }
    }

    private static async Task<Team> TeamAsync(bool bobOwns = false, Action<AlvoApiOptions>? api = null)
    {
        var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, api: api);
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var tenant = await TenantRoutesTests.CreatedAsync(world, ada, "Acme");
        var bobId = await world.UserIdAsync(bob);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.AddMemberAsync(new TenantId(tenant), bobId, bobOwns ? ["dispatcher"] : ["technician"], isOwner: bobOwns);
        return new Team(world, ada, bob, await world.UserIdAsync(ada), bobId, tenant);
    }

    /// <summary>A world with Ada's shared tenant and Bob in it as a technician.</summary>
    private sealed record Team(AccountsWorld World, string Ada, string Bob, UserId AdaId, UserId BobId, Guid Tenant) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => World.DisposeAsync();
    }
}
