using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tenants.SelfService;
using MMLib.Alvo.Tests.Accounts;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// Signing up by invitation over HTTP (spec §3.1, §6.4; S4 plan D3, D5, D7): the token exchange carries
/// <c>invitation_code</c>, and under <c>signUp.mode: invitationOnly</c> the account, its identity and its membership commit
/// in one transaction or not at all — every dead code is one <c>invalid_grant</c> that writes nothing.
/// </summary>
/// <remarks>
/// Ada signs up under <c>open</c> and creates <c>Acme</c> (the S2 D14 shape: an owner exists before the backend closes);
/// then the descriptor is re-applied with <c>invitationOnly</c>. Carol is the stranger Ada invites; Dave, where a fact
/// needs him, already had an account before the backend closed.
/// </remarks>
public sealed class InvitationSignUpTests
{
    private const string TeamTracker = "team-tracker.alvo.json";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    private static readonly string[] _signInTables =
        ["_auth_accounts", "_auth_identities", "_auth_sessions", "_auth_refresh_tokens", "_tenants", "_tenant_members"];

    [Fact]
    public async Task An_invited_person_signs_up_and_is_a_member_at_once()
    {
        await using var team = await InvitationOnlyAsync();
        var (id, code) = await InviteAsync(team, Roles("dispatcher"));

        var carol = await SignedUpAsync(team.World, team.World.IdToken("carol", "carol@gmail.com"), code);

        var acme = await MembershipAsync(team, carol);
        acme["roles"]!.ToJsonString().ShouldBe("""["dispatcher"]""");
        acme["isOwner"]!.GetValue<bool>().ShouldBeFalse();
        using var job = await team.World.AsPersonAsync(HttpMethod.Post, "/api/jobs", carol, Job(team.Tenant), tenant: Header(team.Tenant));
        job.StatusCode.ShouldBe(HttpStatusCode.Created, await job.ReadTextAsync());
        (await UsesAsync(team, id)).ShouldBe(1);
    }

    [Fact]
    public async Task Without_a_code_a_new_person_is_refused_as_before()
    {
        await using var team = await InvitationOnlyAsync();
        await InviteAsync(team, Roles("technician"));

        using var response = await team.World.ExchangeAsync(team.World.IdToken("carol", "carol@gmail.com"));

        var problem = await TokenExchangeTests.RefusalAsync(response, HttpStatusCode.Forbidden, AlvoProblemTypes.SignUpRefused, "access_denied");
        problem["policy"]!.GetValue<string>().ShouldBe("invitationOnly");
        (await team.World.CountAsync("_auth_accounts")).ShouldBe(1, "only Ada");
    }

    /// <summary>
    /// Review Focus 4 (S4 plan D3, D5): every reason a code admits nobody answers one <c>invalid_grant</c>, byte for byte,
    /// and the refused sign-in writes nothing — no account, identity, session, refresh token, personal tenant or membership.
    /// </summary>
    [Fact]
    public async Task A_dead_code_refuses_and_creates_nothing()
    {
        await using var team = await InvitationOnlyAsync();
        var codes = await DeadCodesAsync(team);
        team.World.Clock.Advance(TimeSpan.FromHours(1));
        var before = await CountsAsync(team.World);
        byte[]? first = null;

        foreach (var (reason, code) in codes)
        {
            using var response = await team.World.ExchangeAsync(team.World.IdToken("carol", "carol@gmail.com"), code);

            var body = await response.Content.ReadAsByteArrayAsync(Ct);
            var problem = await TokenExchangeTests.RefusalAsync(response, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_grant");
            problem["error_description"]!.GetValue<string>().ShouldBe(TokenProblems.InvitationDescription, reason);
            Encoding.UTF8.GetString(body).ShouldNotContain(code, Case.Sensitive, reason);
            body.ShouldBe(first ??= body, reason);
            (await CountsAsync(team.World)).ShouldBe(before, reason);
        }
    }

    [Fact]
    public async Task An_email_bound_invitation_signs_up_only_its_trusted_address()
    {
        await using var team = await InvitationOnlyAsync();
        var (id, code) = await InviteAsync(team, new JsonObject { ["roles"] = new JsonArray("technician"), ["email"] = "carol@gmail.com" });

        using var someoneElse = await team.World.ExchangeAsync(team.World.IdToken("erin", "erin@gmail.com"), code);
        using var untrusted = await team.World.ExchangeAsync(team.World.IdToken("carol-unverified", "carol@gmail.com", emailVerified: false), code);
        var carol = await SignedUpAsync(team.World, team.World.IdToken("carol", "Carol@Gmail.com"), code);

        await TokenExchangeTests.RefusalAsync(someoneElse, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_grant");
        await TokenExchangeTests.RefusalAsync(untrusted, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_grant");
        (await MembershipAsync(team, carol))["roles"]!.ToJsonString().ShouldBe("""["technician"]""");
        (await team.World.CountAsync("_auth_accounts")).ShouldBe(2, "Ada and Carol; Erin and the unverified address wrote nothing");
        (await UsesAsync(team, id)).ShouldBe(1);
    }

    /// <summary>S4 plan D7: the exchange accepts the code for whichever account signs in — an existing one too.</summary>
    [Fact]
    public async Task An_existing_person_with_a_code_joins_while_signing_in()
    {
        await using var team = await InvitationOnlyAsync("dave");
        var (id, code) = await InviteAsync(team, Roles("technician"));

        var dave = await SignedUpAsync(team.World, team.World.IdToken("dave", "dave@gmail.com"), code);

        (await MembershipAsync(team, dave))["roles"]!.ToJsonString().ShouldBe("""["technician"]""");
        (await team.World.CountAsync("_auth_accounts")).ShouldBe(2, "Dave signed in; nobody was created");
        (await UsesAsync(team, id)).ShouldBe(1);
    }

    /// <summary>
    /// S4 plan D7, all or nothing: a dead code refuses even an account that would sign in without it — the client asked
    /// for both at once — and the same person signs in without the code.
    /// </summary>
    [Fact]
    public async Task An_existing_person_with_a_dead_code_is_refused()
    {
        await using var team = await InvitationOnlyAsync("dave");
        var sessions = await team.World.CountAsync("_auth_sessions");

        using var refused = await team.World.ExchangeAsync(team.World.IdToken("dave", "dave@gmail.com"), InvitationCode.New().Value);

        await TokenExchangeTests.RefusalAsync(refused, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_grant");
        (await team.World.CountAsync("_auth_sessions")).ShouldBe(sessions, "the refused exchange started no session");
        using var without = await team.World.ExchangeAsync(team.World.IdToken("dave", "dave@gmail.com"));
        without.StatusCode.ShouldBe(HttpStatusCode.OK, await without.ReadTextAsync());
    }

    [Fact]
    public async Task A_code_while_invitations_are_off_is_invalid_request()
    {
        await using var team = await InvitationOnlyAsync("dave");
        var (id, code) = await InviteAsync(team, Roles("technician"));
        await team.World.ReapplyAsync(descriptor => descriptor["tenancy"]!["selfService"]!.AsObject().Remove("invitations"));

        using var stranger = await team.World.ExchangeAsync(team.World.IdToken("carol", "carol@gmail.com"), code);
        using var existing = await team.World.ExchangeAsync(team.World.IdToken("dave", "dave@gmail.com"), code);

        foreach (var response in new[] { stranger, existing })
        {
            var problem = await TokenExchangeTests.RefusalAsync(response, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_request");
            problem["error_description"]!.GetValue<string>().ShouldBe(TokenProblems.InvitationsOffDescription);
        }

        (await team.World.CountAsync("_auth_accounts")).ShouldBe(2);
        await team.World.ReapplyAsync(descriptor => descriptor["tenancy"]!["selfService"]!["invitations"] = true);
        (await UsesAsync(team, id)).ShouldBe(0);
    }

    /// <summary>
    /// S4 plan D5's order: the subject token is verified before the code is looked at, so nobody probes codes anonymously
    /// and an ID token the backend refuses never consumes one.
    /// </summary>
    [Fact]
    public async Task A_code_from_a_refused_subject_token_is_never_consumed()
    {
        await using var team = await InvitationOnlyAsync();
        var (id, code) = await InviteAsync(team, Roles("technician"));
        var forged = team.World.Oidc.Mint("carol", "carol@gmail.com", audience: AccountsWorld.ClientId);

        using var response = await team.World.ExchangeAsync(forged, code);

        var problem = await TokenExchangeTests.RefusalAsync(response, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_request");
        problem["error_description"]!.GetValue<string>().ShouldBe(TokenProblems.SubjectTokenDescription);
        (await UsesAsync(team, id)).ShouldBe(0);
        (await team.World.CountAsync("_auth_accounts")).ShouldBe(1);
    }

    /// <summary>
    /// S4 plan D5: <c>allowedEmailDomains</c> apply under <c>invitationOnly</c> too — a code does not carry an outsider past
    /// a company app's domain list, and refusing them consumes nothing.
    /// </summary>
    [Fact]
    public async Task Allowed_email_domains_refuse_an_invited_outsider()
    {
        await using var team = await InvitationOnlyAsync();
        await team.World.ReapplyAsync(descriptor => descriptor["auth"]!["signUp"]!["allowedEmailDomains"] = new JsonArray("kros.sk"));
        var (id, code) = await InviteAsync(team, Roles("technician"));

        using var outsider = await team.World.ExchangeAsync(team.World.IdToken("carol", "carol@gmail.com"), code);

        var problem = await TokenExchangeTests.RefusalAsync(outsider, HttpStatusCode.Forbidden, AlvoProblemTypes.SignUpRefused, "access_denied");
        problem["policy"]!.GetValue<string>().ShouldBe("allowedEmailDomains");
        (await UsesAsync(team, id)).ShouldBe(0);
        var jan = await SignedUpAsync(team.World, team.World.IdToken("jan", "jan@kros.sk", hd: "kros.sk"), code);
        (await MembershipAsync(team, jan))["roles"]!.ToJsonString().ShouldBe("""["technician"]""");
    }

    /// <summary>The exchange takes a code in any mode (S4 plan D5): under <c>open</c> it creates and joins in one exchange.</summary>
    [Fact]
    public async Task Open_sign_up_with_a_code_creates_and_joins_in_one_exchange()
    {
        await using var team = await TeamAsync();
        var (id, code) = await InviteAsync(team, Roles("technician"));

        var carol = await SignedUpAsync(team.World, team.World.IdToken("carol", "carol@gmail.com"), code);

        (await team.World.MembershipsAsync(carol)).Select(membership => membership!["kind"]!.GetValue<string>())
            .ShouldBe(["personal", "shared"], ignoreOrder: true);
        (await MembershipAsync(team, carol))["roles"]!.ToJsonString().ShouldBe("""["technician"]""");
        (await UsesAsync(team, id)).ShouldBe(1);
    }

    private static async Task<List<(string Reason, string Code)>> DeadCodesAsync(Team team)
    {
        var world = team.World;
        var expiresSoon = new JsonObject
        {
            ["roles"] = new JsonArray(),
            ["expiresAt"] = world.Clock.GetUtcNow().AddHours(1).ToString("O", CultureInfo.InvariantCulture),
        };
        var (revokedId, revoked) = await InviteAsync(team, Roles("technician"));
        using (var revoke = await world.AsPersonAsync(HttpMethod.Delete, $"{InvitationRoutesTests.Path(team.Tenant)}/{revokedId:D}", team.Ada))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var (_, usedUp) = await InviteAsync(team, Roles("technician"));
        await SignedUpAsync(world, world.IdToken("frank", "frank@gmail.com"), usedUp);
        return
        [
            ("unknown", InvitationCode.New().Value),
            ("malformed", "not an invitation code at all"),
            ("expired", (await InviteAsync(team, expiresSoon)).Code),
            ("revoked", revoked),
            ("used up", usedUp),
            ("another email", (await InviteAsync(team, new JsonObject { ["roles"] = new JsonArray(), ["email"] = "someone@example.com" })).Code),
        ];
    }

    private static async Task<string> SignedUpAsync(AccountsWorld world, string idToken, string code)
    {
        using var response = await world.ExchangeAsync(idToken, code);
        var body = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return body["access_token"]!.GetValue<string>();
    }

    private static async Task<JsonObject> MembershipAsync(Team team, string access)
        => (await team.World.MembershipsAsync(access)).Single(membership => membership!["tenantId"]!.GetValue<Guid>() == team.Tenant)!.AsObject();

    private static async Task<int> UsesAsync(Team team, Guid invitation)
    {
        using var response = await team.World.AsPersonAsync(HttpMethod.Get, InvitationRoutesTests.Path(team.Tenant), team.Ada);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        var listed = await response.ReadJsonArrayAsync();
        return listed.Single(item => item["id"]!.GetValue<Guid>() == invitation)["uses"]!.GetValue<int>();
    }

    private static async Task<List<long>> CountsAsync(AccountsWorld world)
    {
        var counts = new List<long>();
        foreach (var table in _signInTables)
        {
            counts.Add(await world.CountAsync(table));
        }

        return counts;
    }

    private static Task<(Guid Id, string Code)> InviteAsync(Team team, JsonObject body)
        => InvitationRoutesTests.InvitedAsync(team.World, team.Ada, team.Tenant, body);

    private static JsonObject Roles(params string[] roles) => new() { ["roles"] = new JsonArray([.. roles.Select(role => (JsonNode)role)]) };

    private static JsonObject Job(Guid tenant) => new() { ["title"] = "Fix the boiler", ["tenant_id"] = Header(tenant) };

    private static string Header(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Team> TeamAsync(params string[] existing)
    {
        var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker);
        var (ada, _) = await world.SignInAsync("ada");
        foreach (var subject in existing)
        {
            await world.SignInAsync(subject, email: $"{subject}@gmail.com");
        }

        var tenant = await TenantRoutesTests.CreatedAsync(world, ada, "Acme");
        return new Team(world, ada, tenant);
    }

    private static async Task<Team> InvitationOnlyAsync(params string[] existing)
    {
        var team = await TeamAsync(existing);
        await team.World.ReapplyAsync(descriptor => descriptor["auth"]!["signUp"]!["mode"] = "invitationOnly");
        return team;
    }

    /// <summary>A world with Ada's shared tenant <c>Acme</c>.</summary>
    private sealed record Team(AccountsWorld World, string Ada, Guid Tenant) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => World.DisposeAsync();
    }
}
