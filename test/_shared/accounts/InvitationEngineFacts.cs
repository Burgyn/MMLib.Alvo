using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Accounts;

/// <summary>
/// Invitations end to end, the same facts on SQLite and on PostgreSQL (spec §6.3, §6.4, §14 AC4–AC5; S4 Review Focus 1
/// and 2): an owner invites, anyone holding the code previews it, a person accepts and acts in the tenant on their next
/// request — and the races and walls around it. Two people accepting one single-use code make one member; two strangers
/// signing up with it under <c>invitationOnly</c> make one account; a demoted owner's invitations are dead at once; an
/// invitation grants only assignable roles, never <c>admin</c>, and a tenant role it grants never reaches global data or
/// management.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each engine contributes only <see cref="Engine"/></b>, as in <see cref="TenancyEngineFacts"/>. Everything here goes
/// over HTTP: this file is linked into the PostgreSQL suite, which is granted none of the internals (a dead code is any
/// text the backend never issued, so none is minted here).
/// </para>
/// <para>
/// <b>The races run ten times each.</b> On PostgreSQL the two requests really overlap in the engine and the invitation's
/// conditional <c>UPDATE</c> is what decides (S4 plan D4 step 5); on SQLite the write gate serialises them, and the fact
/// still pins the answer pair.
/// </para>
/// </remarks>
public abstract class InvitationEngineFacts
{
    private const string Tenants = "/auth/v1/tenants";

    private const string Preview = "/auth/v1/invitations/preview";

    private const string Accept = "/auth/v1/invitations/accept";

    private const string Jobs = "/api/jobs";

    private const string TeamTracker = "team-tracker.alvo.json";

    private const int Iterations = 10;

    /// <summary>Gets the engine every world in this suite runs on.</summary>
    protected abstract AlvoApiEngine Engine { get; }

    [Fact]
    public async Task Level_three_with_invitations_end_to_end()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        await CreatedJobAsync(world, ada, acme, "Boiler service");
        var (_, technician) = await InvitedAsync(world, ada, acme, Roles("technician"));
        var (unused, unusedCode) = await InvitedAsync(world, ada, acme, Roles("technician"));

        using (var previewed = await PreviewAsync(world, technician))
        {
            previewed.StatusCode.ShouldBe(HttpStatusCode.OK, await previewed.ReadTextAsync());
            (await previewed.ReadJsonObjectAsync())["tenantName"]!.GetValue<string>().ShouldBe("Acme");
        }

        var (carol, _) = await world.SignInAsync("carol", email: "carol@gmail.com");
        await AcceptedAsync(world, carol, technician);
        (await ItemsAsync(world, carol, acme)).ShouldHaveSingleItem()!["title"]!.GetValue<string>().ShouldBe("Boiler service");
        using (var own = await JobAsync(world, carol, acme, "Carol's own"))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await own.ReadTextAsync());
        }

        await RevokedAsync(world, ada, acme, unused);
        using (var dead = await PreviewAsync(world, unusedCode))
        {
            dead.StatusCode.ShouldBe(HttpStatusCode.NotFound, await dead.ReadTextAsync());
        }

        using var removed = await world.AsPersonAsync(HttpMethod.Delete, MemberPath(acme, await world.UserIdAsync(carol)), ada);
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.ReadTextAsync());
        await InvalidTokenAsync(await world.AsPersonAsync(HttpMethod.Get, Jobs, carol, tenant: Header(acme)));
    }

    [Fact]
    public async Task Invitation_only_end_to_end()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        await world.ReapplyAsync(descriptor => descriptor["auth"]!["signUp"]!["mode"] = "invitationOnly");
        var (_, code) = await InvitedAsync(world, ada, acme, Roles("technician"));

        using (var withoutCode = await world.ExchangeAsync(world.IdToken("carol", "carol@gmail.com")))
        using (var deadCode = await world.ExchangeAsync(world.IdToken("carol", "carol@gmail.com"), NeverIssued()))
        {
            withoutCode.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await withoutCode.ReadTextAsync());
            (await withoutCode.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.SignUpRefused);
            deadCode.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await deadCode.ReadTextAsync());
            (await deadCode.ReadJsonObjectAsync())["error"]!.GetValue<string>().ShouldBe("invalid_grant");
        }

        (await world.CountAsync("_auth_accounts")).ShouldBe(1, "only Ada");
        var carol = await SignedUpAsync(world, "carol", code);
        (await ItemsAsync(world, carol, acme)).ShouldBeEmpty("a technician of Acme lists its jobs at once, and there are none");
        (await world.CountAsync("_auth_accounts")).ShouldBe(2);
    }

    /// <summary>S4 Review Focus 1 over HTTP: one single-use code, two people at once — one member, the other the dead 404.</summary>
    [Fact]
    public async Task Two_people_accepting_a_single_use_invitation_at_once_make_one_member()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");

        for (var i = 0; i < Iterations; i++)
        {
            var (_, code) = await InvitedAsync(world, ada, acme, Roles("technician"));
            var (first, _) = await world.SignInAsync($"first-{i}", email: $"first-{i}@gmail.com");
            var (second, _) = await world.SignInAsync($"second-{i}", email: $"second-{i}@gmail.com");

            var answers = await Task.WhenAll(AcceptAsync(world, first, code), AcceptAsync(world, second, code));

            await OneWinsAsync(answers, HttpStatusCode.OK, HttpStatusCode.NotFound, i);
            (await MembersAsync(world, ada, acme)).Count.ShouldBe(i + 2, $"iteration {i}: Ada and one invitee per code");
        }
    }

    /// <summary>S4 Review Focus 1 through the exchange: under <c>invitationOnly</c>, one single-use code makes one account.</summary>
    [Fact]
    public async Task Two_strangers_signing_up_with_one_single_use_code_at_once_make_one_account()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        await world.ReapplyAsync(descriptor => descriptor["auth"]!["signUp"]!["mode"] = "invitationOnly");

        for (var i = 0; i < Iterations; i++)
        {
            var (_, code) = await InvitedAsync(world, ada, acme, Roles("technician"));

            var answers = await Task.WhenAll(
                world.ExchangeAsync(world.IdToken($"first-{i}", $"first-{i}@gmail.com"), code),
                world.ExchangeAsync(world.IdToken($"second-{i}", $"second-{i}@gmail.com"), code));

            await OneWinsAsync(answers, HttpStatusCode.OK, HttpStatusCode.BadRequest, i);
            (await world.CountAsync("_auth_accounts")).ShouldBe(i + 2, $"iteration {i}: Ada and one account per code");
        }
    }

    /// <summary>S4 plan D6: an owner's invitations die in the transaction that takes their ownership away.</summary>
    [Fact]
    public async Task A_demoted_owner_s_invitations_are_dead_on_the_next_request()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        var bobId = await world.UserIdAsync(bob);
        await world.AddMemberAsync(new TenantId(acme), bobId, [], isOwner: true);
        var (bobs, code) = await InvitedAsync(world, bob, acme, Roles("technician"));

        using (var demoted = await world.AsPersonAsync(HttpMethod.Patch, MemberPath(acme, bobId), ada, new JsonObject { ["isOwner"] = false }))
        {
            demoted.StatusCode.ShouldBe(HttpStatusCode.OK, await demoted.ReadTextAsync());
        }

        var (carol, _) = await world.SignInAsync("carol", email: "carol@gmail.com");
        using var previewed = await PreviewAsync(world, code);
        using var accepted = await AcceptAsync(world, carol, code);

        previewed.StatusCode.ShouldBe(HttpStatusCode.NotFound, await previewed.ReadTextAsync());
        accepted.StatusCode.ShouldBe(HttpStatusCode.NotFound, await accepted.ReadTextAsync());
        (await InvitationsAsync(world, ada, acme)).Single(item => item["id"]!.GetValue<Guid>() == bobs)["status"]!.GetValue<string>()
            .ShouldBe("revoked");
    }

    /// <summary>
    /// Spec §14 AC4 through an invitation: <c>admin</c> and a project role are refused at create, and a stored role the
    /// descriptor stops making assignable is gone on the invitee's next request (S3 plan D5: stored ∩ <c>assignableRoles</c>).
    /// </summary>
    [Fact]
    public async Task An_invitation_grants_only_assignable_roles_and_never_admin()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");

        foreach (var role in new[] { "admin", "owner" })
        {
            using var refused = await world.AsPersonAsync(HttpMethod.Post, InvitationsPath(acme), ada, Roles(role));
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{role}: {await refused.ReadTextAsync()}");
        }

        await CreatedJobAsync(world, ada, acme, "Boiler service");
        var (_, code) = await InvitedAsync(world, ada, acme, Roles("technician"));
        var (carol, _) = await world.SignInAsync("carol", email: "carol@gmail.com");
        await AcceptedAsync(world, carol, code);
        (await ItemsAsync(world, carol, acme)).ShouldHaveSingleItem("a technician lists Acme's jobs");

        await world.ReapplyAsync(descriptor => descriptor["tenancy"]!["selfService"]!["assignableRoles"] = new JsonArray("dispatcher"));

        (await ItemsAsync(world, carol, acme)).ShouldBeEmpty("the stored technician no longer counts; the list rule admits no row");
        var membership = (await world.MembershipsAsync(carol)).Single(item => item!["tenantId"]!.GetValue<Guid>() == acme)!;
        membership["roles"]!.AsArray().ShouldBeEmpty();
    }

    /// <summary>
    /// Spec §6.4's seventh bullet through an invitation: the invited <c>dispatcher</c> counts on Acme's scoped rows only —
    /// never on the global <c>regions</c>, and never on management, which answers the Bearer as it answers nobody.
    /// </summary>
    [Fact]
    public async Task An_invited_tenant_role_never_reaches_a_global_entity_or_management()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        var (_, code) = await InvitedAsync(world, ada, acme, Roles("dispatcher"));
        var (carol, _) = await world.SignInAsync("carol", email: "carol@gmail.com");
        await AcceptedAsync(world, carol, code);
        await CreatedJobAsync(world, carol, acme, "Dispatched by Carol");
        const string Descriptor = "/management/projects/team-tracker/descriptor";

        using var region = await world.AsPersonAsync(HttpMethod.Post, "/api/regions", carol, new JsonObject { ["code"] = "SOUTH" }, tenant: Header(acme));
        using var management = await world.AsPersonAsync(HttpMethod.Get, Descriptor, carol, tenant: Header(acme));
        using var anonymous = await world.Api.SendAsync(HttpMethod.Get, Descriptor);

        region.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await region.ReadTextAsync());
        management.StatusCode.ShouldBe(anonymous.StatusCode, await management.ReadTextAsync());
        management.IsSuccessStatusCode.ShouldBeFalse();
        (await management.ReadProblemTypeAsync()).ShouldBe(await anonymous.ReadProblemTypeAsync());
    }

    private static string NeverIssued() => $"alvo_inv_{Guid.NewGuid():N}"[..31];

    private static string Header(Guid tenant) => tenant.ToString("D");

    private static string InvitationsPath(Guid tenant) => $"{Tenants}/{tenant:D}/invitations";

    private static string MemberPath(Guid tenant, UserId member) => $"{Tenants}/{tenant:D}/members/{member.Value:D}";

    private static JsonObject Roles(params string[] roles) => new() { ["roles"] = new JsonArray([.. roles.Select(role => (JsonNode)role)]) };

    private static JsonObject Code(string code) => new() { ["code"] = code };

    private Task<AccountsWorld> TeamAsync() => AccountsWorld.StartAsync(Engine, descriptor: TeamTracker);

    private static async Task<Guid> CreatedTenantAsync(AccountsWorld world, string access, string name)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Post, Tenants, access, new JsonObject { ["name"] = name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["tenantId"]!.GetValue<Guid>();
    }

    private static async Task<(Guid Id, string Code)> InvitedAsync(AccountsWorld world, string access, Guid tenant, JsonObject body)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Post, InvitationsPath(tenant), access, body);
        var created = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, created.ToJsonString());
        return (created["id"]!.GetValue<Guid>(), created["code"]!.GetValue<string>());
    }

    private static async Task RevokedAsync(AccountsWorld world, string access, Guid tenant, Guid invitation)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Delete, $"{InvitationsPath(tenant)}/{invitation:D}", access);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.ReadTextAsync());
    }

    private static Task<HttpResponseMessage> PreviewAsync(AccountsWorld world, string code)
        => world.Api.SendAsync(HttpMethod.Post, Preview, body: Code(code));

    private static Task<HttpResponseMessage> AcceptAsync(AccountsWorld world, string access, string code)
        => world.AsPersonAsync(HttpMethod.Post, Accept, access, Code(code));

    private static async Task AcceptedAsync(AccountsWorld world, string access, string code)
    {
        using var response = await AcceptAsync(world, access, code);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
    }

    private static async Task<string> SignedUpAsync(AccountsWorld world, string subject, string code)
    {
        using var response = await world.ExchangeAsync(world.IdToken(subject, $"{subject}@gmail.com"), code);
        var body = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return body["access_token"]!.GetValue<string>();
    }

    private static async Task OneWinsAsync(HttpResponseMessage[] answers, HttpStatusCode won, HttpStatusCode lost, int iteration)
    {
        try
        {
            var statuses = answers.Select(answer => answer.StatusCode).Order().ToArray();
            statuses.ShouldBe(new[] { won, lost }.Order().ToArray(), $"iteration {iteration}: {await answers[0].ReadTextAsync()} | {await answers[1].ReadTextAsync()}");
        }
        finally
        {
            Array.ForEach(answers, answer => answer.Dispose());
        }
    }

    private static Task<HttpResponseMessage> JobAsync(AccountsWorld world, string access, Guid tenant, string title)
        => world.AsPersonAsync(HttpMethod.Post, Jobs, access, new JsonObject { ["title"] = title, ["tenant_id"] = Header(tenant) }, tenant: Header(tenant));

    private static async Task CreatedJobAsync(AccountsWorld world, string access, Guid tenant, string title)
    {
        using var created = await JobAsync(world, access, tenant, title);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
    }

    private static async Task<JsonArray> ItemsAsync(AccountsWorld world, string access, Guid tenant)
    {
        using var list = await world.AsPersonAsync(HttpMethod.Get, Jobs, access, tenant: Header(tenant));
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.ReadTextAsync());
        return (await list.ReadJsonObjectAsync())["items"]!.AsArray();
    }

    private static async Task<IReadOnlyList<JsonObject>> MembersAsync(AccountsWorld world, string access, Guid tenant)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Get, $"{Tenants}/{tenant:D}/members", access);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return await response.ReadJsonArrayAsync();
    }

    private static async Task<IReadOnlyList<JsonObject>> InvitationsAsync(AccountsWorld world, string access, Guid tenant)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Get, InvitationsPath(tenant), access);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return await response.ReadJsonArrayAsync();
    }

    private static async Task InvalidTokenAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.ReadTextAsync());
            response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer error=\"invalid_token\"");
        }
    }
}
