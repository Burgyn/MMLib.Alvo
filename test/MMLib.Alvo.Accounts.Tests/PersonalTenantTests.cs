using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tests.Accounts;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// Level 2 (spec §3, §6.5; S3 plan D5, D6): the first exchange creates a person's own tenant, a request without a tenant
/// header acts in it, two people's tenants never meet, and a descriptor that adds <c>personalTenant</c> after people signed
/// up reaches each of them at their next refresh.
/// </summary>
public sealed class PersonalTenantTests
{
    private const string Entries = "/api/time_entries";

    private const string Descriptor = "personal-tracker.alvo.json";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task A_first_exchange_creates_the_personal_tenant()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: Descriptor);
        var (access, _) = await world.SignInAsync("sub-1");

        var memberships = await world.MembershipsAsync(access);

        var personal = memberships.ShouldHaveSingleItem()!.AsObject();
        personal.Select(member => member.Key).ShouldBe(["tenantId", "name", "kind", "roles", "isOwner"], ignoreOrder: true);
        personal["kind"]!.GetValue<string>().ShouldBe("personal");
        personal["name"]!.GetValue<string>().ShouldBe("Ada Lovelace");
        personal["roles"]!.AsArray().ShouldBeEmpty();
        personal["isOwner"]!.GetValue<bool>().ShouldBeTrue();
        (await world.CountAsync(AlvoFrameworkTables.TenantsSuffix)).ShouldBe(1);
    }

    [Fact]
    public async Task Without_a_header_a_person_writes_into_their_personal_tenant()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: Descriptor);
        var (access, _) = await world.SignInAsync("sub-1");
        var personal = await world.PersonalTenantAsync(access);

        using var created = await world.AsPersonAsync(HttpMethod.Post, Entries, access, Entry(30, personal));
        using var list = await world.AsPersonAsync(HttpMethod.Get, Entries, access);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var row = (await list.ReadJsonObjectAsync())["items"]!.AsArray().ShouldHaveSingleItem()!;
        row["tenant_id"]!.GetValue<Guid>().ShouldBe(personal.Value);
    }

    [Fact]
    public async Task Two_people_s_personal_tenants_never_see_each_other()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: Descriptor);
        var (ada, _) = await world.SignInAsync("ada");
        var (bob, _) = await world.SignInAsync("bob", email: "bob@gmail.com");
        using var created = await world.AsPersonAsync(HttpMethod.Post, Entries, ada, Entry(30, await world.PersonalTenantAsync(ada)));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());

        using var bobs = await world.AsPersonAsync(HttpMethod.Get, Entries, bob);
        using var adas = await world.AsPersonAsync(HttpMethod.Get, Entries, ada);
        using var intruding = await world.AsPersonAsync(
            HttpMethod.Get, Entries, bob, tenant: (await world.PersonalTenantAsync(ada)).Value.ToString());

        (await world.PersonalTenantAsync(ada)).ShouldNotBe(await world.PersonalTenantAsync(bob));
        (await bobs.ReadJsonObjectAsync())["items"]!.AsArray().ShouldBeEmpty();
        (await adas.ReadJsonObjectAsync())["items"]!.AsArray().Count.ShouldBe(1);
        await PersonResolverTests.InvalidTokenAsync(intruding);
    }

    [Fact]
    public async Task A_personal_tenant_added_after_sign_up_appears_at_the_next_refresh()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, app: PersonResolverTests.MapWhoAmI);
        var (access, refresh) = await world.SignInAsync("sub-1");
        (await world.MembershipsAsync(access)).ShouldBeEmpty();

        await world.ReapplyAsync(descriptor => descriptor["tenancy"] = new JsonObject
        {
            ["enabled"] = true,
            ["selfService"] = new JsonObject { ["personalTenant"] = new JsonObject { ["roles"] = new JsonArray() } },
        });
        var before = await PersonResolverTests.WhoAmIAsync(world, access);
        (await world.MembershipsAsync(access)).ShouldBeEmpty("nothing created it yet");
        using var refreshed = await world.RefreshAsync(refresh);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK, await refreshed.ReadTextAsync());
        var after = await PersonResolverTests.WhoAmIAsync(world, access);
        var personal = await world.PersonalTenantAsync(access);
        using var created = await world.AsPersonAsync(HttpMethod.Post, Entries, access, Entry(30, personal));

        before["tenant"].ShouldBeNull("between the apply and the refresh the person has no tenant");
        after["tenant"]!.GetValue<Guid>().ShouldBe(personal.Value, "the next request acts in it, on the same access token");
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await created.ReadJsonObjectAsync())["tenant_id"]!.GetValue<Guid>().ShouldBe(personal.Value);
        (await world.CountAsync(AlvoFrameworkTables.TenantsSuffix)).ShouldBe(1);
    }

    /// <summary>A time entry, echoing the tenant it is written into as a scoped create must.</summary>
    private static JsonObject Entry(int minutes, TenantId tenant) =>
        new() { ["minutes"] = minutes, ["tenant_id"] = tenant.Value };
}
