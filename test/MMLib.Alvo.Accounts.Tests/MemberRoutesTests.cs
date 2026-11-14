using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// A tenant's members (spec §6.3, §6.4, §14 AC4; S3 plan D5, D7, D8): owners change roles within <c>assignableRoles</c> and
/// ownership, anyone leaves, nobody changes their own membership, the last owner stays, a personal tenant never changes —
/// and every change counts on the member's next request.
/// </summary>
/// <remarks>
/// Ada creates <c>Acme</c> over <c>POST /tenants</c> (owner, <c>dispatcher</c>); Bob is seeded into it with no role
/// (members arrive through invitations, S4 — S3 plan D15).
/// </remarks>
public sealed class MemberRoutesTests
{
    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    private static readonly string[] _memberKeys = ["userId", "name", "avatarUrl", "roles", "isOwner"];

    [Fact]
    public async Task An_owner_grants_a_role_and_the_member_s_next_request_carries_it()
    {
        await using var team = await TeamAsync();
        Names((await PersonResolverTests.WhoAmIAsync(team.World, team.Bob, team.Tenant))["tenantRoles"]).ShouldBeEmpty();

        using var granted = await PatchAsync(team, team.Ada, team.BobId, new JsonObject { ["roles"] = new JsonArray("technician") });

        granted.StatusCode.ShouldBe(HttpStatusCode.OK, await granted.ReadTextAsync());
        granted.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var member = await granted.ReadJsonObjectAsync();
        member["userId"]!.GetValue<Guid>().ShouldBe(team.BobId.Value);
        Names(member["roles"]).ShouldBe(["technician"]);
        member["isOwner"]!.GetValue<bool>().ShouldBeFalse();
        Names((await PersonResolverTests.WhoAmIAsync(team.World, team.Bob, team.Tenant))["tenantRoles"]).ShouldBe(["technician"]);
    }

    [Theory]
    [InlineData("admin", "/roles/0")]
    [InlineData("owner", "/roles/0")]
    [InlineData("auditor", "/roles/0")]
    [InlineData("technician,technician", "/roles/1")]
    public async Task A_role_outside_assignable_roles_is_refused(string roles, string at)
    {
        await using var team = await TeamAsync();
        var sent = new JsonArray([.. roles.Split(',').Select(role => (JsonNode)role)]);

        using var refused = await PatchAsync(team, team.Ada, team.BobId, new JsonObject { ["roles"] = sent });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var problem = await refused.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.BaseUri + AlvoProblemTypes.Validation);
        problem["violations"]!.AsArray().ShouldHaveSingleItem()!["pointer"]!.GetValue<string>().ShouldBe(at);
        if (!roles.Contains(',', StringComparison.Ordinal))
        {
            problem["detail"]!.GetValue<string>().ShouldContain("dispatcher, technician");
        }

        Names((await MemberAsync(team, team.Ada, team.BobId))["roles"]).ShouldBeEmpty();
    }

    [Fact]
    public async Task Nobody_patches_their_own_membership()
    {
        await using var team = await TeamAsync();

        using var refused = await PatchAsync(team, team.Ada, team.AdaId, new JsonObject { ["roles"] = new JsonArray("technician") });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        (await refused.ReadProblemDetailAsync()).ShouldBe("Nobody changes their own membership; another owner can, and you can leave.");
        Names((await MemberAsync(team, team.Ada, team.AdaId))["roles"]).ShouldBe(["dispatcher"]);
    }

    [Fact]
    public async Task A_member_cannot_change_another_member()
    {
        await using var team = await TeamAsync();

        using var refused = await PatchAsync(team, team.Bob, team.AdaId, new JsonObject { ["isOwner"] = false });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        (await refused.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        (await MemberAsync(team, team.Ada, team.AdaId))["isOwner"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Removing_a_member_refuses_their_next_request_in_that_tenant()
    {
        await using var team = await TeamAsync();
        (await PersonResolverTests.WhoAmIAsync(team.World, team.Bob, team.Tenant))["tenant"]!.GetValue<Guid>().ShouldBe(team.Tenant.Value);

        using var removed = await DeleteAsync(team, team.Ada, team.BobId);
        using var named = await team.World.AsPersonAsync(HttpMethod.Get, "/whoami", team.Bob, tenant: team.Tenant.Value.ToString());
        var unnamed = await PersonResolverTests.WhoAmIAsync(team.World, team.Bob);

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.ReadTextAsync());
        removed.Headers.CacheControl!.NoStore.ShouldBeTrue();
        await PersonResolverTests.InvalidTokenAsync(named);
        unnamed["tenant"]!.GetValue<Guid>().ShouldBe((await team.World.PersonalTenantAsync(team.Bob)).Value);
    }

    [Fact]
    public async Task A_member_leaves_by_deleting_themself()
    {
        await using var team = await TeamAsync();

        using var left = await DeleteAsync(team, team.Bob, team.BobId);

        left.StatusCode.ShouldBe(HttpStatusCode.NoContent, await left.ReadTextAsync());
        (await MembersAsync(team, team.Ada)).Select(member => member["userId"]!.GetValue<Guid>()).ShouldBe([team.AdaId.Value]);
        (await team.World.MembershipsAsync(team.Bob)).ShouldHaveSingleItem()!["kind"]!.GetValue<string>().ShouldBe("personal");
    }

    /// <summary>
    /// Spec §6.4: the last owner stays. Leaving is the one way the last owner can ask to go, and it is 409
    /// <c>last-owner</c>; every other way is refused earlier, by who may ask — demoting oneself is 403 (nobody changes their
    /// own membership, judged first, AC4) and a non-owner removing or demoting the owner is 403 (owners only). Nothing
    /// changes either way.
    /// </summary>
    [Fact]
    public async Task The_last_owner_cannot_leave_be_removed_or_be_demoted()
    {
        await using var team = await TeamAsync();

        using var leave = await DeleteAsync(team, team.Ada, team.AdaId);
        using var demoteSelf = await PatchAsync(team, team.Ada, team.AdaId, new JsonObject { ["isOwner"] = false });
        using var removedByMember = await DeleteAsync(team, team.Bob, team.AdaId);
        using var demotedByMember = await PatchAsync(team, team.Bob, team.AdaId, new JsonObject { ["isOwner"] = false });

        leave.StatusCode.ShouldBe(HttpStatusCode.Conflict, await leave.ReadTextAsync());
        (await leave.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.LastOwner);
        foreach (var refused in new[] { demoteSelf, removedByMember, demotedByMember })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        }

        (await MemberAsync(team, team.Ada, team.AdaId))["isOwner"]!.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>
    /// Ownership is handed over by promoting a second owner; then the first may leave, and the new sole owner is held by the
    /// same rule — their own leaving is 409 <c>last-owner</c>.
    /// </summary>
    [Fact]
    public async Task Promoting_a_second_owner_lets_the_first_leave()
    {
        await using var team = await TeamAsync();

        using var promoted = await PatchAsync(team, team.Ada, team.BobId, new JsonObject { ["isOwner"] = true });
        using var left = await DeleteAsync(team, team.Ada, team.AdaId);
        using var lastLeaves = await DeleteAsync(team, team.Bob, team.BobId);

        promoted.StatusCode.ShouldBe(HttpStatusCode.OK, await promoted.ReadTextAsync());
        (await promoted.ReadJsonObjectAsync())["isOwner"]!.GetValue<bool>().ShouldBeTrue();
        left.StatusCode.ShouldBe(HttpStatusCode.NoContent, await left.ReadTextAsync());
        lastLeaves.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await lastLeaves.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.LastOwner);
        var members = await MembersAsync(team, team.Bob);
        members.ShouldHaveSingleItem()["userId"]!.GetValue<Guid>().ShouldBe(team.BobId.Value);
        members.Single()["isOwner"]!.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>
    /// A personal tenant has exactly one member, its owner (spec §6.1): listed alone; leaving it and changing its one
    /// membership are 409 <c>conflict</c> — the registry judges the tenant's kind before it looks for the member, so a
    /// change naming another id is the same 409.
    /// </summary>
    [Fact]
    public async Task A_personal_tenant_lists_one_member_and_refuses_changes()
    {
        await using var team = await TeamAsync();
        var personal = await team.World.PersonalTenantAsync(team.Ada);

        using var listed = await team.World.AsPersonAsync(HttpMethod.Get, $"{TenantRoutesTests.Tenants}/{personal}/members", team.Ada);
        using var leave = await team.World.AsPersonAsync(HttpMethod.Delete, $"{TenantRoutesTests.Tenants}/{personal}/members/{team.AdaId}", team.Ada);
        using var patch = await team.World.AsPersonAsync(
            HttpMethod.Patch, $"{TenantRoutesTests.Tenants}/{personal}/members/{team.BobId}", team.Ada, new JsonObject { ["isOwner"] = true });

        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.ReadTextAsync());
        var only = (await listed.ReadJsonArrayAsync()).ShouldHaveSingleItem();
        only["userId"]!.GetValue<Guid>().ShouldBe(team.AdaId.Value);
        only["isOwner"]!.GetValue<bool>().ShouldBeTrue();
        foreach (var refused in new[] { leave, patch })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.ReadTextAsync());
            (await refused.ReadProblemDetailAsync()).ShouldBe("A personal tenant has exactly one member, its owner.");
        }
    }

    [Fact]
    public async Task Members_show_display_names_and_never_an_email()
    {
        await using var team = await TeamAsync();
        using var named = await team.World.AsPersonAsync(HttpMethod.Patch, "/auth/v1/user", team.Bob, new JsonObject { ["name"] = "Bob Builder" });
        named.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");
        var carolId = await team.World.UserIdAsync(carol);
        team.World.Clock.Advance(TimeSpan.FromMinutes(1));
        await team.World.AddMemberAsync(team.Tenant, carolId, ["technician", "owner", "admin", "auditor"]);

        using var response = await team.World.AsPersonAsync(HttpMethod.Get, MembersPath(team), team.Bob);

        var text = await response.ReadTextAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        text.ShouldNotContain("@gmail.com");
        var members = await response.ReadJsonArrayAsync();
        members.ShouldAllBe(member => member.Select(pair => pair.Key).SequenceEqual(_memberKeys));
        members.Select(member => member["userId"]!.GetValue<Guid>()).ShouldBe([team.AdaId.Value, team.BobId.Value, carolId.Value]);
        members.Select(member => member["name"]!.GetValue<string>()).ShouldBe(["Ada Lovelace", "Bob Builder", "Ada Lovelace"]);
        Names(members[0]["roles"]).ShouldBe(["dispatcher"]);
        Names(members[2]["roles"]).ShouldBe(["technician"], "a stored name outside assignableRoles is never shown (D5)");
    }

    /// <summary>
    /// GitHub's rule (S3 plan D7): another person's shared tenant, another person's <em>personal</em> tenant — where an
    /// owner's own change would be 409 — and an id naming nothing all answer a stranger the same 404, byte for byte.
    /// </summary>
    [Fact]
    public async Task A_non_member_gets_404_on_every_member_route()
    {
        await using var team = await TeamAsync();
        var (carol, _) = await team.World.SignInAsync("carol", email: "carol@gmail.com");
        var unknown = Guid.NewGuid();
        var adasPersonal = (await team.World.PersonalTenantAsync(team.Ada)).Value;

        var bodies = new List<byte[]>();
        foreach (var tenant in new[] { team.Tenant.Value, adasPersonal, unknown })
        {
            foreach (var request in Requests(tenant, team.BobId.Value))
            {
                using var response = await team.World.AsPersonAsync(request.Method, request.Path, carol, request.Body);
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.ReadTextAsync());
                (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.NotFound);
                bodies.Add(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }
        }

        bodies.Select(Convert.ToBase64String).Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        Names((await MemberAsync(team, team.Ada, team.BobId))["roles"]).ShouldBeEmpty("nothing changed");
    }

    /// <summary>
    /// S3 plan D5 on the routes: a membership the applied descriptor does not honour — a personal one while
    /// <c>personalTenant</c> is off — counts on no Data API request, so no route manages it either. Renaming the tenant and
    /// every member route answer the stranger's 404, byte for byte; the shared tenant beside it is untouched.
    /// </summary>
    [Fact]
    public async Task An_unhonoured_personal_membership_is_managed_by_no_route()
    {
        await using var team = await TeamAsync();
        var personal = (await team.World.PersonalTenantAsync(team.Ada)).Value;
        await team.World.ReapplyAsync(descriptor => descriptor["tenancy"]!["selfService"]!.AsObject().Remove("personalTenant"));

        var bodies = new List<byte[]>();
        foreach (var tenant in new[] { personal, Guid.NewGuid() })
        {
            (HttpMethod Method, string Path, JsonObject? Body) rename = (HttpMethod.Patch, $"{TenantRoutesTests.Tenants}/{tenant:D}", new JsonObject { ["name"] = "Mine" });
            foreach (var request in Requests(tenant, team.AdaId.Value).Append(rename))
            {
                using var response = await team.World.AsPersonAsync(request.Method, request.Path, team.Ada, request.Body);
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{request.Method} {request.Path}: {await response.ReadTextAsync()}");
                bodies.Add(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }
        }

        bodies.Select(Convert.ToBase64String).Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        (await MembersAsync(team, team.Ada)).Count.ShouldBe(2, "the shared tenant is still Ada's to manage");
    }

    private static IEnumerable<(HttpMethod Method, string Path, JsonObject? Body)> Requests(Guid tenant, Guid member) =>
    [
        (HttpMethod.Get, $"{TenantRoutesTests.Tenants}/{tenant:D}/members", null),
        (HttpMethod.Patch, $"{TenantRoutesTests.Tenants}/{tenant:D}/members/{member:D}", new JsonObject { ["roles"] = new JsonArray("technician") }),
        (HttpMethod.Delete, $"{TenantRoutesTests.Tenants}/{tenant:D}/members/{member:D}", null),
    ];

    private static string[] Names(JsonNode? roles) =>
        [.. roles!.AsArray().Select(role => role!.GetValue<string>()).Order(StringComparer.Ordinal)];

    private static async Task<Team> TeamAsync()
    {
        var world = await AccountsWorld.StartAsync(_engine, descriptor: "team-tracker.alvo.json", app: PersonResolverTests.MapWhoAmI);
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var tenant = new TenantId(await TenantRoutesTests.CreatedAsync(world, ada, "Acme"));
        var bobId = await world.UserIdAsync(bob);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.AddMemberAsync(tenant, bobId, []);
        return new Team(world, ada, bob, await world.UserIdAsync(ada), bobId, tenant);
    }

    private static string MembersPath(Team team) => $"{TenantRoutesTests.Tenants}/{team.Tenant.Value:D}/members";

    private static Task<HttpResponseMessage> PatchAsync(Team team, string access, UserId member, JsonObject body)
        => team.World.AsPersonAsync(HttpMethod.Patch, $"{MembersPath(team)}/{member.Value:D}", access, body);

    private static Task<HttpResponseMessage> DeleteAsync(Team team, string access, UserId member)
        => team.World.AsPersonAsync(HttpMethod.Delete, $"{MembersPath(team)}/{member.Value:D}", access);

    private static async Task<IReadOnlyList<JsonObject>> MembersAsync(Team team, string access)
    {
        using var response = await team.World.AsPersonAsync(HttpMethod.Get, MembersPath(team), access);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return await response.ReadJsonArrayAsync();
    }

    private static async Task<JsonObject> MemberAsync(Team team, string access, UserId member) =>
        (await MembersAsync(team, access)).Single(row => row["userId"]!.GetValue<Guid>() == member.Value);

    /// <summary>A world with Ada's shared tenant and Bob in it.</summary>
    private sealed record Team(AccountsWorld World, string Ada, string Bob, UserId AdaId, UserId BobId, TenantId Tenant) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => World.DisposeAsync();
    }
}
