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
/// Previewing and accepting an invitation (spec §6.3, §6.4, §14 AC5; S4 plan D3, D8, D10, D11): anyone holding a code
/// previews it, a signed-in person accepts it and acts in the tenant on their next request, the code travels only in a
/// body — and every code that admits nobody answers one 404, byte for byte the S3 routes' own.
/// </summary>
/// <remarks>Ada creates <c>Acme</c> over <c>POST /tenants</c> and invites; Carol, Dave and Erin are the invitees.</remarks>
public sealed class InvitationAcceptTests
{
    private const string TeamTracker = "team-tracker.alvo.json";

    private const string PreviewPath = "/auth/v1/invitations/preview";

    private const string AcceptPath = "/auth/v1/invitations/accept";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    public static TheoryData<string, string> Bodies => new()
    {
        { """{}""", "/code" },
        { """{"code":1}""", "/code" },
        { """{"code":null}""", "/code" },
        { """{"code":""}""", "/code" },
        { """{"code":"a\ud800"}""", "/code" },
        { """{"code":"zzsecret","extra":1}""", "/extra" },
    };

    [Fact]
    public async Task Anyone_holding_the_code_previews_it_without_signing_in()
    {
        var integration = new TestApiKey("integration", ["authenticated"], ["*:read", "*:write"]);
        await using var team = await TeamAsync(keys: [integration]);
        var (_, code) = await InvitationRoutesTests.InvitedAsync(
            team.World, team.Ada, team.Tenant, new JsonObject { ["roles"] = new JsonArray("technician", "dispatcher"), ["email"] = "x@y.z" });

        using var anonymous = await PreviewAsync(team.World, code);
        using var keyed = await team.World.Api.SendAsync(HttpMethod.Post, PreviewPath, integration, body: Code(code));

        var text = await anonymous.ReadTextAsync();
        anonymous.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        anonymous.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var expires = team.World.Clock.GetUtcNow().AddDays(7).AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss+00:00", CultureInfo.InvariantCulture);
        text.ShouldBe($$"""{"tenantName":"Acme","inviterName":"Ada Lovelace","roles":["dispatcher","technician"],"isOwner":false,"expiresAt":"{{expires}}","emailBound":true}""");
        keyed.StatusCode.ShouldBe(HttpStatusCode.OK, await keyed.ReadTextAsync());
        (await keyed.ReadTextAsync()).ShouldBe(text);
    }

    [Fact]
    public async Task A_person_accepts_and_their_next_request_acts_in_the_tenant_with_its_roles()
    {
        await using var team = await TeamAsync();
        var (_, dispatcherCode) = await InvitationRoutesTests.InvitedAsync(team.World, team.Ada, team.Tenant, Roles("dispatcher"));
        var (_, technicianCode) = await InvitationRoutesTests.InvitedAsync(team.World, team.Ada, team.Tenant, Roles("technician"));
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");
        var (dave, _) = await team.World.SignInAsync("dave", email: "dave@gmail.com");

        using var accepted = await AcceptAsync(team.World, carol, dispatcherCode);
        using var technician = await AcceptAsync(team.World, dave, technicianCode);
        using var dispatched = await team.World.AsPersonAsync(HttpMethod.Post, "/api/jobs", carol, Job(team.Tenant), tenant: Header(team.Tenant));
        using var refused = await team.World.AsPersonAsync(HttpMethod.Post, "/api/jobs", dave, Job(team.Tenant), tenant: Header(team.Tenant));

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.ReadTextAsync());
        accepted.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await accepted.ReadTextAsync()).ShouldBe(
            $$"""{"tenantId":"{{team.Tenant:D}}","name":"Acme","kind":"shared","roles":["dispatcher"],"isOwner":false}""");
        technician.StatusCode.ShouldBe(HttpStatusCode.OK, await technician.ReadTextAsync());
        dispatched.StatusCode.ShouldBe(HttpStatusCode.Created, await dispatched.ReadTextAsync());
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
    }

    [Fact]
    public async Task Accepting_again_is_200_with_the_roles_unchanged()
    {
        await using var team = await TeamAsync();
        var (id, code) = await InvitationRoutesTests.InvitedAsync(team.World, team.Ada, team.Tenant, Roles("technician"));
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");
        using var first = await AcceptAsync(team.World, carol, code);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.ReadTextAsync());
        var carolId = await team.World.UserIdAsync(carol);
        using var changed = await team.World.AsPersonAsync(
            HttpMethod.Patch, $"{TenantRoutesTests.Tenants}/{team.Tenant:D}/members/{carolId.Value:D}", team.Ada,
            new JsonObject { ["roles"] = new JsonArray("dispatcher") });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.ReadTextAsync());

        using var again = await AcceptAsync(team.World, carol, code);

        again.StatusCode.ShouldBe(HttpStatusCode.OK, await again.ReadTextAsync());
        (await again.ReadJsonObjectAsync())["roles"]!.ToJsonString().ShouldBe("""["dispatcher"]""");
        var listed = await ListedAsync(team);
        listed.Single(item => item["id"]!.GetValue<Guid>() == id)["uses"]!.GetValue<int>().ShouldBe(1, "a second accept consumes nothing");
    }

    [Fact]
    public async Task An_owner_granting_invitation_makes_an_owner()
    {
        await using var team = await TeamAsync();
        var (_, code) = await InvitationRoutesTests.InvitedAsync(
            team.World, team.Ada, team.Tenant, new JsonObject { ["roles"] = new JsonArray(), ["isOwner"] = true });
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");

        using var accepted = await AcceptAsync(team.World, carol, code);
        using var invites = await team.World.AsPersonAsync(
            HttpMethod.Post, InvitationRoutesTests.Path(team.Tenant), carol, InvitationRoutesTests.Technician());

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.ReadTextAsync());
        (await accepted.ReadJsonObjectAsync())["isOwner"]!.GetValue<bool>().ShouldBeTrue();
        invites.StatusCode.ShouldBe(HttpStatusCode.Created, await invites.ReadTextAsync());
    }

    /// <summary>
    /// Review Focus 5 (S4 plan D3): every reason a code admits nobody answers the S3 routes' no-such-tenant 404, byte for
    /// byte, on preview and on accept — and none of them carries the code. The anonymous preview cannot judge an email
    /// binding (D8: it has no someone), so the two email reasons preview as live; that exclusion is asserted, not skipped.
    /// </summary>
    [Fact]
    public async Task Every_dead_reason_answers_one_byte_identical_404()
    {
        await using var team = await TeamAsync();
        var codes = await DeadCodesAsync(team);
        team.World.Clock.Advance(TimeSpan.FromHours(1));
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");
        var erin = await SignInUntrustedAsync(team.World, "erin", "erin@gmail.com");
        using var noSuchTenant = await team.World.AsPersonAsync(HttpMethod.Get, $"{TenantRoutesTests.Tenants}/{Guid.NewGuid():D}/members", carol);
        var expected = await noSuchTenant.Content.ReadAsByteArrayAsync(Ct);
        noSuchTenant.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        foreach (var (reason, code) in codes)
        {
            using var accepted = await AcceptAsync(team.World, reason == "untrusted email" ? erin : carol, code);
            await DeadAsync(accepted, expected, code, $"accept: {reason}");
            using var previewed = await PreviewAsync(team.World, code);
            if (reason.EndsWith("email", StringComparison.Ordinal))
            {
                previewed.StatusCode.ShouldBe(HttpStatusCode.OK, $"preview: {reason} — D8, the preview has no someone");
                (await previewed.ReadJsonObjectAsync())["emailBound"]!.GetValue<bool>().ShouldBeTrue();
                continue;
            }

            await DeadAsync(previewed, expected, code, $"preview: {reason}");
        }

        (await team.World.MembershipsAsync(carol)).Count.ShouldBe(1, "Carol joined nothing");
        (await team.World.MembershipsAsync(erin)).Count.ShouldBe(1, "Erin joined nothing");
    }

    [Fact]
    public async Task An_email_bound_invitation_admits_its_trusted_address_in_any_case()
    {
        await using var team = await TeamAsync();
        var (_, code) = await InvitationRoutesTests.InvitedAsync(
            team.World, team.Ada, team.Tenant, new JsonObject { ["roles"] = new JsonArray("technician"), ["email"] = "carol@gmail.com" });
        var (carol, _) = await team.World.SignInAsync("carol", email: "Carol@Gmail.com");

        using var accepted = await AcceptAsync(team.World, carol, code);

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.ReadTextAsync());
        (await accepted.ReadJsonObjectAsync())["tenantId"]!.GetValue<Guid>().ShouldBe(team.Tenant);
    }

    [Fact]
    public async Task Removing_an_owner_kills_their_invitations_over_http()
    {
        await using var team = await TeamAsync();
        var (bob, _) = await team.World.SignInAsync("bob", email: "bob@gmail.com");
        var bobId = await team.World.UserIdAsync(bob);
        await team.World.AddMemberAsync(new TenantId(team.Tenant), bobId, [], isOwner: true);
        var (_, bobs) = await InvitationRoutesTests.InvitedAsync(team.World, bob, team.Tenant, Roles("technician"));
        var (_, adas) = await InvitationRoutesTests.InvitedAsync(team.World, team.Ada, team.Tenant, Roles("technician"));

        using var removed = await team.World.AsPersonAsync(
            HttpMethod.Delete, $"{TenantRoutesTests.Tenants}/{team.Tenant:D}/members/{bobId.Value:D}", team.Ada);
        using var bobsPreview = await PreviewAsync(team.World, bobs);
        using var adasPreview = await PreviewAsync(team.World, adas);

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.ReadTextAsync());
        bobsPreview.StatusCode.ShouldBe(HttpStatusCode.NotFound, await bobsPreview.ReadTextAsync());
        adasPreview.StatusCode.ShouldBe(HttpStatusCode.OK, await adasPreview.ReadTextAsync());
    }

    [Fact]
    public async Task Accept_needs_a_person()
    {
        var integration = new TestApiKey("integration", ["authenticated"], ["*:read", "*:write"]);
        await using var team = await TeamAsync(keys: [integration]);
        var (_, code) = await InvitationRoutesTests.InvitedAsync(team.World, team.Ada, team.Tenant, Roles("technician"));

        using var anonymous = await team.World.Api.SendAsync(HttpMethod.Post, AcceptPath, body: Code(code));
        using var keyed = await team.World.Api.SendAsync(HttpMethod.Post, AcceptPath, integration, body: Code(code));

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await anonymous.ReadTextAsync());
        anonymous.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        keyed.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await keyed.ReadTextAsync());
        (await keyed.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        using var preview = await PreviewAsync(team.World, code);
        preview.StatusCode.ShouldBe(HttpStatusCode.OK, "nothing was consumed");
    }

    /// <summary>
    /// A body that presents no string code is a 422 at its pointer on both routes — never a 500 (a missing code is never
    /// hashed), never the dead 404, and never repeating what was sent.
    /// </summary>
    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task A_body_without_one_string_code_is_a_validation_problem(string json, string at)
    {
        await using var team = await TeamAsync();

        foreach (var (path, access) in new[] { (PreviewPath, (string?)null), (AcceptPath, team.Ada) })
        {
            using var response = await RawAsync(team.World, path, access, new StringContent(json, Encoding.UTF8, "application/json"));

            var text = await response.ReadTextAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{path}: {text}");
            (await response.ReadViolationsAsync()).Select(violation => violation.Pointer).ShouldBe([at], path);
            text.ShouldNotContain("zzsecret");
        }
    }

    [Fact]
    public async Task A_non_json_body_is_415_on_both_routes()
    {
        await using var team = await TeamAsync();

        foreach (var (path, access) in new[] { (PreviewPath, (string?)null), (AcceptPath, team.Ada) })
        {
            using var response = await RawAsync(team.World, path, access, new StringContent("code=x", Encoding.UTF8, "application/x-www-form-urlencoded"));

            response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, await response.ReadTextAsync());
        }
    }

    [Fact]
    public async Task Without_invitations_preview_and_accept_are_404_naming_its_key()
    {
        await using var team = await TeamAsync();
        var (_, code) = await InvitationRoutesTests.InvitedAsync(team.World, team.Ada, team.Tenant, Roles("technician"));
        await team.World.ReapplyAsync(descriptor => descriptor["tenancy"]!["selfService"]!.AsObject().Remove("invitations"));

        using (var preview = await PreviewAsync(team.World, code))
        using (var accept = await AcceptAsync(team.World, team.Ada, code))
        {
            await TenantRoutesTests.FeatureDisabledAsync(preview, SelfServiceProblems.InvitationsKey);
            await TenantRoutesTests.FeatureDisabledAsync(accept, SelfServiceProblems.InvitationsKey);
        }

        await team.World.ReapplyAsync(descriptor => descriptor["tenancy"]!.AsObject().Remove("selfService"));

        using var offPreview = await PreviewAsync(team.World, code);
        using var offAccept = await AcceptAsync(team.World, team.Ada, code);
        await TenantRoutesTests.FeatureDisabledAsync(offPreview, SelfServiceProblems.SelfServiceKey);
        await TenantRoutesTests.FeatureDisabledAsync(offAccept, SelfServiceProblems.SelfServiceKey);
    }

    [Fact]
    public async Task Preview_and_accept_are_charged_the_invitations_budget()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, api: api => api.RateLimits.Invitations = 2);
        var (ada, _) = await world.SignInAsync("ada");

        using var first = await PreviewAsync(world, "alvo_inv_nothing");
        using var second = await AcceptAsync(world, ada, "alvo_inv_nothing");
        using var third = await PreviewAsync(world, "alvo_inv_nothing");

        first.StatusCode.ShouldBe(HttpStatusCode.NotFound, await first.ReadTextAsync());
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound, await second.ReadTextAsync());
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, await third.ReadTextAsync());
        third.Headers.Contains("Retry-After").ShouldBeTrue();
        third.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    private static async Task<List<(string Reason, string Code)>> DeadCodesAsync(Team team)
    {
        var world = team.World;
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var bobId = await world.UserIdAsync(bob);
        await world.AddMemberAsync(new TenantId(team.Tenant), bobId, [], isOwner: true);
        var expiresSoon = new JsonObject
        {
            ["roles"] = new JsonArray(),
            ["expiresAt"] = world.Clock.GetUtcNow().AddHours(1).ToString("O", CultureInfo.InvariantCulture),
        };
        var codes = new List<(string Reason, string Code)>
        {
            ("unknown", InvitationCode.New().Value),
            ("malformed", "not an invitation code at all"),
            ("expired", (await InvitationRoutesTests.InvitedAsync(world, team.Ada, team.Tenant, expiresSoon)).Code),
            ("revoked", await RevokedAsync(team)),
            ("used up", await UsedUpAsync(team)),
            ("another email", (await InvitationRoutesTests.InvitedAsync(world, team.Ada, team.Tenant, Bound("someone@example.com"))).Code),
            ("untrusted email", (await InvitationRoutesTests.InvitedAsync(world, team.Ada, team.Tenant, Bound("erin@gmail.com"))).Code),
            ("personal tenant", await PersonalAsync(team)),
            ("creator demoted", await DemotedCreatorAsync(team, bob, bobId)),
        };
        return codes;
    }

    private static async Task<string> RevokedAsync(Team team)
    {
        var (id, code) = await InvitationRoutesTests.InvitedAsync(team.World, team.Ada, team.Tenant, Roles("technician"));
        using var revoked = await team.World.AsPersonAsync(HttpMethod.Delete, $"{InvitationRoutesTests.Path(team.Tenant)}/{id:D}", team.Ada);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return code;
    }

    private static async Task<string> UsedUpAsync(Team team)
    {
        var (_, code) = await InvitationRoutesTests.InvitedAsync(team.World, team.Ada, team.Tenant, Roles("technician"));
        var (dave, _) = await team.World.SignInAsync("dave", email: "dave@gmail.com");
        using var accepted = await AcceptAsync(team.World, dave, code);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.ReadTextAsync());
        return code;
    }

    private static async Task<string> PersonalAsync(Team team)
    {
        var code = InvitationCode.New();
        await team.World.AddInvitationAsync(await team.World.PersonalTenantAsync(team.Ada), await team.World.UserIdAsync(team.Ada), code.Hashed);
        return code.Value;
    }

    private static async Task<string> DemotedCreatorAsync(Team team, string bob, UserId bobId)
    {
        var (_, code) = await InvitationRoutesTests.InvitedAsync(team.World, bob, team.Tenant, Roles("technician"));
        using var demoted = await team.World.AsPersonAsync(
            HttpMethod.Patch, $"{TenantRoutesTests.Tenants}/{team.Tenant:D}/members/{bobId.Value:D}", team.Ada, new JsonObject { ["isOwner"] = false });
        demoted.StatusCode.ShouldBe(HttpStatusCode.OK, await demoted.ReadTextAsync());
        return code;
    }

    private static async Task DeadAsync(HttpResponseMessage response, byte[] expected, string code, string because)
    {
        var body = await response.Content.ReadAsByteArrayAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{because}: {Encoding.UTF8.GetString(body)}");
        response.Content.Headers.ContentType!.ToString().ShouldBe("application/problem+json", because);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue(because);
        body.ShouldBe(expected, because);
        Encoding.UTF8.GetString(body).ShouldNotContain(code, Case.Sensitive, because);
    }

    private static async Task<string> SignInUntrustedAsync(AccountsWorld world, string subject, string email)
    {
        using var response = await world.ExchangeAsync(world.IdToken(subject, email, emailVerified: false));
        var body = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return body["access_token"]!.GetValue<string>();
    }

    private static async Task<IReadOnlyList<JsonObject>> ListedAsync(Team team)
    {
        using var response = await team.World.AsPersonAsync(HttpMethod.Get, InvitationRoutesTests.Path(team.Tenant), team.Ada);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return await response.ReadJsonArrayAsync();
    }

    private static Task<HttpResponseMessage> PreviewAsync(AccountsWorld world, string code)
        => world.Api.SendAsync(HttpMethod.Post, PreviewPath, body: Code(code));

    private static Task<HttpResponseMessage> AcceptAsync(AccountsWorld world, string access, string code)
        => world.AsPersonAsync(HttpMethod.Post, AcceptPath, access, Code(code));

    private static Task<HttpResponseMessage> RawAsync(AccountsWorld world, string path, string? access, HttpContent content) =>
        access is null
            ? world.Api.SendRawAsync(HttpMethod.Post, path, content: content)
            : TenantRoutesTests.RawAsync(world, HttpMethod.Post, path, access, content);

    private static JsonObject Code(string code) => new() { ["code"] = code };

    private static JsonObject Roles(params string[] roles) => new() { ["roles"] = new JsonArray([.. roles.Select(role => (JsonNode)role)]) };

    private static JsonObject Bound(string email) => new() { ["roles"] = new JsonArray(), ["email"] = email };

    private static JsonObject Job(Guid tenant) => new() { ["title"] = "Fix the boiler", ["tenant_id"] = Header(tenant) };

    private static string Header(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Team> TeamAsync(IReadOnlyList<TestApiKey>? keys = null)
    {
        var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, keys: keys);
        var (ada, _) = await world.SignInAsync("ada");
        var tenant = await TenantRoutesTests.CreatedAsync(world, ada, "Acme");
        return new Team(world, ada, tenant);
    }

    /// <summary>A world with Ada's shared tenant <c>Acme</c>.</summary>
    private sealed record Team(AccountsWorld World, string Ada, Guid Tenant) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => World.DisposeAsync();
    }
}
