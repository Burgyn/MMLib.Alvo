using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Accounts;

/// <summary>
/// Levels 2 and 3 of spec §3 end to end, the same facts on SQLite and on PostgreSQL (spec §14 AC2–AC4, AC7; S3 Review
/// Focus 1 and 5): a person's own tenant, shared tenants a person creates and manages — and the walls between tenants.
/// A tenant the person is not in refuses the whole principal; a tenant role counts only in its own tenant and never on
/// global data or management; the owner flag is never a role; the last owner stays; a personal tenant takes no members;
/// creating stops at the cap; and an API key beside all of it works exactly as before.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each engine contributes only <see cref="Engine"/></b>, as in <see cref="AccountsEngineFacts"/>: a fact written in one
/// leg is a fact the other never has to satisfy.
/// </para>
/// <para>
/// <b>A second member is seeded</b> (<see cref="AccountsWorld.AddMemberAsync"/>): in S3 members arrive only through
/// invitations, which are S4 (S3 plan D15). A scoped create echoes its <c>tenant_id</c> (the Data API's contract), so every
/// write here names the tenant it is sent in.
/// </para>
/// </remarks>
public abstract class TenancyEngineFacts
{
    private const string Tenants = "/auth/v1/tenants";

    private const string Jobs = "/api/jobs";

    private const string Regions = "/api/regions";

    private const string TeamTracker = "team-tracker.alvo.json";

    private static readonly Guid _integrationTenant = Guid.Parse("7a1c0de0-0000-4000-8000-000000000001");

    private static readonly TestApiKey _integration = new("tenancy-integration", ["authenticated", "dispatcher"], ["*:read", "*:write"], _integrationTenant);

    /// <summary>Gets the engine every world in this suite runs on.</summary>
    protected abstract AlvoApiEngine Engine { get; }

    [Fact]
    public async Task Level_two_end_to_end()
    {
        await using var world = await AccountsWorld.StartAsync(Engine, descriptor: "personal-tracker.alvo.json");
        var (ada, _) = await world.SignInAsync("ada");
        var personal = (await world.MembershipsAsync(ada)).ShouldHaveSingleItem()!;
        personal["kind"]!.GetValue<string>().ShouldBe("personal");
        var tenant = personal["tenantId"]!.GetValue<Guid>();

        using var created = await world.AsPersonAsync(
            HttpMethod.Post, "/api/time_entries", ada, new JsonObject { ["minutes"] = 30, ["tenant_id"] = tenant.ToString() });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await ItemsAsync(world, "/api/time_entries", ada)).ShouldHaveSingleItem()!["tenant_id"]!.GetValue<Guid>().ShouldBe(tenant);
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        (await ItemsAsync(world, "/api/time_entries", bob)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Level_three_end_to_end()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        await CreatedJobAsync(world, ada, acme, "Boiler service");
        using var renamed = await world.AsPersonAsync(HttpMethod.Patch, $"{Tenants}/{acme:D}", ada, new JsonObject { ["name"] = "Acme Field Service" });
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK, await renamed.ReadTextAsync());
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var bobId = await world.UserIdAsync(bob);
        await world.AddMemberAsync(new TenantId(acme), bobId, []);

        using var granted = await world.AsPersonAsync(
            HttpMethod.Patch, MemberPath(acme, bobId), ada, new JsonObject { ["roles"] = new JsonArray("technician") });
        granted.StatusCode.ShouldBe(HttpStatusCode.OK, await granted.ReadTextAsync());
        (await ItemsAsync(world, Jobs, bob, acme)).ShouldHaveSingleItem()!["title"]!.GetValue<string>().ShouldBe("Boiler service");
        using var bobsOwn = await JobAsync(world, bob, acme, "Bob's own");
        bobsOwn.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await bobsOwn.ReadTextAsync());

        using var removed = await world.AsPersonAsync(HttpMethod.Delete, MemberPath(acme, bobId), ada);
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.ReadTextAsync());
        await InvalidTokenAsync(await world.AsPersonAsync(HttpMethod.Get, Jobs, bob, tenant: Header(acme)));
    }

    [Fact]
    public async Task A_header_naming_a_tenant_you_are_not_in_refuses_the_whole_principal()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var adasShared = await CreatedTenantAsync(world, ada, "Acme");
        var adasPersonal = (await world.PersonalTenantAsync(ada)).Value;
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");

        foreach (var header in new[] { Header(adasShared), Header(adasPersonal), Header(Guid.NewGuid()), "not-a-tenant" })
        {
            using var response = await world.AsPersonAsync(HttpMethod.Get, Jobs, bob, tenant: header);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{header}: {await response.ReadTextAsync()}");
            (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Unauthenticated);
        }

        (await ItemsAsync(world, Jobs, ada, adasShared)).ShouldBeEmpty("the owner still reads their own tenant");
    }

    [Fact]
    public async Task A_tenant_role_from_one_tenant_is_absent_in_another()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var adas = await CreatedTenantAsync(world, ada, "Acme");
        var bobs = await CreatedTenantAsync(world, bob, "Bobco");
        await world.AddMemberAsync(new TenantId(bobs), await world.UserIdAsync(ada), ["technician"]);

        using var inA = await JobAsync(world, ada, adas, "Dispatched in A");
        using var inB = await JobAsync(world, ada, bobs, "Dispatched in B");

        inA.StatusCode.ShouldBe(HttpStatusCode.Created, await inA.ReadTextAsync());
        inB.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await inB.ReadTextAsync());
        (await ItemsAsync(world, Jobs, ada, bobs)).ShouldBeEmpty("a technician of B lists B's jobs, and there are none");
    }

    [Fact]
    public async Task Rows_written_in_one_tenant_are_invisible_in_another()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var first = await CreatedTenantAsync(world, ada, "First");
        var second = await CreatedTenantAsync(world, ada, "Second");
        using var created = await JobAsync(world, ada, first, "Only in the first");
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var job = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

        using var read = await world.AsPersonAsync(HttpMethod.Get, $"{Jobs}/{job:D}", ada, tenant: Header(second));
        using var changed = await world.AsPersonAsync(
            HttpMethod.Patch, $"{Jobs}/{job:D}", ada, new JsonObject { ["title"] = "Moved" }, tenant: Header(second));

        (await ItemsAsync(world, Jobs, ada, second)).ShouldBeEmpty();
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound, await read.ReadTextAsync());
        changed.StatusCode.ShouldBe(HttpStatusCode.NotFound, await changed.ReadTextAsync());
        (await ItemsAsync(world, Jobs, ada, first)).ShouldHaveSingleItem()!["title"]!.GetValue<string>().ShouldBe("Only in the first");
    }

    /// <summary>
    /// S3 Review Focus 5: <c>dispatcher</c> is a tenant role the person gave themself by creating a tenant, so it counts only
    /// on that tenant's scoped rows (the S1 path rule). <c>regions</c> is global: neither a direct write nor the
    /// <c>jobs.exists(…)</c> term — true here, since a job in the tenant references the region — admits it, because past a
    /// global hop only project roles count. The integration key's project <c>dispatcher</c> is admitted, so the rule is live.
    /// The refused <c>PATCH</c> is 404, not 403: an <c>update</c> rule filters the rows a caller may change, and a row it
    /// excludes is one the write cannot find — the Data API's existing contract, which the plan's "403" overlooked.
    /// </summary>
    [Fact]
    public async Task A_self_made_dispatcher_cannot_write_global_regions()
    {
        await using var world = await TeamAsync(keys: [_integration]);
        using var seeded = await world.Api.SendAsync(HttpMethod.Post, Regions, _integration, body: new JsonObject { ["code"] = "NORTH" });
        seeded.StatusCode.ShouldBe(HttpStatusCode.Created, await seeded.ReadTextAsync());
        var region = (await seeded.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        await CreatedJobAsync(world, ada, acme, "In the north", region);

        using var created = await world.AsPersonAsync(HttpMethod.Post, Regions, ada, new JsonObject { ["code"] = "SOUTH" }, tenant: Header(acme));
        using var changed = await world.AsPersonAsync(
            HttpMethod.Patch, $"{Regions}/{region:D}", ada, new JsonObject { ["code"] = "MINE" }, tenant: Header(acme));
        using var unnamed = await world.AsPersonAsync(HttpMethod.Post, Regions, ada, new JsonObject { ["code"] = "WEST" });
        using var project = await world.Api.SendAsync(
            HttpMethod.Patch, $"{Regions}/{region:D}", _integration, body: new JsonObject { ["code"] = "NORTH2" });

        created.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await created.ReadTextAsync());
        changed.StatusCode.ShouldBe(HttpStatusCode.NotFound, await changed.ReadTextAsync());
        unnamed.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await unnamed.ReadTextAsync());
        project.StatusCode.ShouldBe(HttpStatusCode.OK, await project.ReadTextAsync());
        using var read = await world.Api.SendAsync(HttpMethod.Get, $"{Regions}/{region:D}", _integration);
        (await read.ReadJsonObjectAsync())["code"]!.GetValue<string>().ShouldBe("NORTH2", "only the project role changed it");
    }

    /// <summary>
    /// <c>owner</c> is a project role in the fixture, and <c>notes</c> admits only it. Owning a tenant never mints it into
    /// <c>@user.roles</c>, and neither does a stored role name the descriptor never made assignable (S3 plan D5: stored ∩
    /// <c>assignableRoles</c>) — Bob's row says <c>owner</c>, and it counts for nothing.
    /// </summary>
    [Fact]
    public async Task The_owner_flag_is_never_a_role()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        await world.AddMemberAsync(new TenantId(acme), await world.UserIdAsync(bob), ["technician", "owner"]);

        using var byOwner = await NoteAsync(world, ada, acme);
        using var byStoredName = await NoteAsync(world, bob, acme);

        byOwner.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await byOwner.ReadTextAsync());
        byStoredName.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await byStoredName.ReadTextAsync());
        (await MembersAsync(world, ada, acme)).Single(member => member["isOwner"]!.GetValue<bool>())["userId"]!.GetValue<Guid>()
            .ShouldBe((await world.UserIdAsync(ada)).Value, "Ada does own Acme");
    }

    [Fact]
    public async Task A_tenant_role_never_reaches_management()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        const string Descriptor = "/management/projects/team-tracker/descriptor";

        using var asDispatcher = await world.AsPersonAsync(HttpMethod.Get, Descriptor, ada, tenant: Header(acme));
        using var anonymous = await world.Api.SendAsync(HttpMethod.Get, Descriptor);

        asDispatcher.StatusCode.ShouldBe(anonymous.StatusCode, await asDispatcher.ReadTextAsync());
        asDispatcher.IsSuccessStatusCode.ShouldBeFalse();
        (await asDispatcher.ReadProblemTypeAsync()).ShouldBe(await anonymous.ReadProblemTypeAsync());
    }

    /// <summary>
    /// Spec §6.4: the last owner stays. Leaving is the one way the last owner can ask to go (409 <c>last-owner</c>); demoting
    /// oneself is 403 (nobody changes their own membership, AC4) and a member removing or demoting the owner is 403 (owners
    /// only) — S3 plan Task 4's recorded deviation. Nothing changes either way.
    /// </summary>
    [Fact]
    public async Task The_last_owner_cannot_leave_be_removed_or_be_demoted()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        var (adaId, bobId) = (await world.UserIdAsync(ada), await world.UserIdAsync(bob));
        await world.AddMemberAsync(new TenantId(acme), bobId, []);
        var demote = new JsonObject { ["isOwner"] = false };

        using var leave = await world.AsPersonAsync(HttpMethod.Delete, MemberPath(acme, adaId), ada);
        using var demoteSelf = await world.AsPersonAsync(HttpMethod.Patch, MemberPath(acme, adaId), ada, demote);
        using var removedByMember = await world.AsPersonAsync(HttpMethod.Delete, MemberPath(acme, adaId), bob);
        using var demotedByMember = await world.AsPersonAsync(HttpMethod.Patch, MemberPath(acme, adaId), bob, demote);

        leave.StatusCode.ShouldBe(HttpStatusCode.Conflict, await leave.ReadTextAsync());
        (await leave.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.LastOwner);
        foreach (var refused in new[] { demoteSelf, removedByMember, demotedByMember })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        }

        var owners = (await MembersAsync(world, ada, acme)).Where(member => member["isOwner"]!.GetValue<bool>());
        owners.ShouldHaveSingleItem()["userId"]!.GetValue<Guid>().ShouldBe(adaId.Value);
    }

    [Fact]
    public async Task A_personal_tenant_takes_no_members()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        var personal = (await world.PersonalTenantAsync(ada)).Value;
        var (adaId, bobId) = (await world.UserIdAsync(ada), await world.UserIdAsync(bob));

        using var promote = await world.AsPersonAsync(HttpMethod.Patch, MemberPath(personal, bobId), ada, new JsonObject { ["isOwner"] = true });
        using var leave = await world.AsPersonAsync(HttpMethod.Delete, MemberPath(personal, adaId), ada);
        using var stranger = await world.AsPersonAsync(HttpMethod.Delete, MemberPath(personal, adaId), bob);

        foreach (var refused in new[] { promote, leave })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.ReadTextAsync());
            (await refused.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Conflict);
        }

        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound, await stranger.ReadTextAsync());
        (await MembersAsync(world, ada, personal)).ShouldHaveSingleItem()["userId"]!.GetValue<Guid>().ShouldBe(adaId.Value);
    }

    /// <summary>
    /// <c>maxPerAccount</c> is 2 in the fixture: five creates at once leave exactly two shared tenants (the store's
    /// per-account lock, S3 Review Focus 3, now over HTTP), every other answer 409 <c>tenant-limit</c> naming the cap.
    /// </summary>
    [Fact]
    public async Task Creating_tenants_stops_at_the_cap()
    {
        await using var world = await TeamAsync();
        var (ada, _) = await world.SignInAsync("ada");

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => world.AsPersonAsync(
            HttpMethod.Post, Tenants, ada, new JsonObject { ["name"] = $"Tenant {i}" })));

        try
        {
            answers.Count(answer => answer.StatusCode == HttpStatusCode.Created).ShouldBe(2);
            foreach (var refused in answers.Where(answer => answer.StatusCode != HttpStatusCode.Created))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.ReadTextAsync());
                (await refused.ReadJsonObjectAsync())["maxPerAccount"]!.GetValue<int>().ShouldBe(2);
            }

            (await world.MembershipsAsync(ada)).Count(membership => membership!["kind"]!.GetValue<string>() == "shared").ShouldBe(2);
        }
        finally
        {
            Array.ForEach(answers, answer => answer.Dispose());
        }
    }

    /// <summary>Spec §14 AC7: self-service changes nothing for an API key — its tenant, its rows, its refusals.</summary>
    [Fact]
    public async Task An_api_key_beside_self_service_behaves_exactly_as_before()
    {
        await using var world = await TeamAsync(keys: [_integration]);
        var (ada, _) = await world.SignInAsync("ada");
        var acme = await CreatedTenantAsync(world, ada, "Acme");
        await CreatedJobAsync(world, ada, acme, "Ada's");

        using var created = await world.Api.SendAsync(
            HttpMethod.Post, Jobs, _integration, body: new JsonObject { ["title"] = "The key's", ["tenant_id"] = Header(_integrationTenant) });
        using var listed = await world.Api.SendAsync(HttpMethod.Get, Jobs, _integration);
        using var tenants = await world.Api.SendAsync(HttpMethod.Get, Tenants, _integration);
        using var both = await world.Api.SendAsync(
            HttpMethod.Get, Jobs, _integration, headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {ada}")]);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.ReadTextAsync());
        var row = (await listed.ReadJsonObjectAsync())["items"]!.AsArray().ShouldHaveSingleItem()!;
        (row["title"]!.GetValue<string>(), row["tenant_id"]!.GetValue<Guid>()).ShouldBe(("The key's", _integrationTenant));
        tenants.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await tenants.ReadTextAsync());
        both.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await both.ReadTextAsync());
        (await both.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Unauthenticated);
    }

    private static string Header(Guid tenant) => tenant.ToString("D");

    private static string MemberPath(Guid tenant, UserId member) => $"{Tenants}/{tenant:D}/members/{member.Value:D}";

    private Task<AccountsWorld> TeamAsync(IReadOnlyList<TestApiKey>? keys = null)
        => AccountsWorld.StartAsync(Engine, descriptor: TeamTracker, keys: keys);

    private static async Task<Guid> CreatedTenantAsync(AccountsWorld world, string access, string name)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Post, Tenants, access, new JsonObject { ["name"] = name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["tenantId"]!.GetValue<Guid>();
    }

    private static Task<HttpResponseMessage> JobAsync(AccountsWorld world, string access, Guid tenant, string title, Guid? region = null)
    {
        var job = new JsonObject { ["title"] = title, ["tenant_id"] = Header(tenant) };
        if (region is { } id)
        {
            job["region_id"] = id.ToString("D");
        }

        return world.AsPersonAsync(HttpMethod.Post, Jobs, access, job, tenant: Header(tenant));
    }

    private static async Task CreatedJobAsync(AccountsWorld world, string access, Guid tenant, string title, Guid? region = null)
    {
        using var created = await JobAsync(world, access, tenant, title, region);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
    }

    private static Task<HttpResponseMessage> NoteAsync(AccountsWorld world, string access, Guid tenant) => world.AsPersonAsync(
        HttpMethod.Post, "/api/notes", access, new JsonObject { ["text"] = "Owners only", ["tenant_id"] = Header(tenant) }, tenant: Header(tenant));

    private static async Task<JsonArray> ItemsAsync(AccountsWorld world, string path, string access, Guid? tenant = null)
    {
        using var list = await world.AsPersonAsync(HttpMethod.Get, path, access, tenant: tenant is { } id ? Header(id) : null);
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.ReadTextAsync());
        return (await list.ReadJsonObjectAsync())["items"]!.AsArray();
    }

    private static async Task<IReadOnlyList<JsonObject>> MembersAsync(AccountsWorld world, string access, Guid tenant)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Get, $"{Tenants}/{tenant:D}/members", access);
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
