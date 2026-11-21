using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// #543 over HTTP: banning a tenant's only owner answers the tenants it left nobody to manage, and an operator makes
/// another account an owner through <c>PUT {m}/projects/{project}/tenants/{tenant}/owners/{account}</c> — without enabling
/// the banned person. Over <c>team-tracker</c>, whose <c>selfService</c> assigns <c>dispatcher</c> and <c>technician</c>.
/// </summary>
public sealed class OwnershipGrantTests
{
    private const string Tenants = "/auth/v1/tenants";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task A_ban_names_the_tenant_it_orphaned_and_an_operator_gives_it_another_owner()
    {
        await using var world = await StartAsync();
        var (ada, _) = await world.SignInAsync("sub-ada");
        var (bob, _) = await world.SignInAsync("sub-bob");
        var tenant = await CreatedAsync(world, ada, "Acme");

        using var banned = await BanAsync(world, AccountOf(ada));
        using var granted = await GrantAsync(world, tenant, AccountOf(bob), new JsonObject { ["roles"] = new JsonArray("dispatcher") });
        using var renamed = await world.AsPersonAsync(HttpMethod.Patch, $"{Tenants}/{tenant:D}", bob, new JsonObject { ["name"] = "Acme 2" });
        using var removed = await world.AsPersonAsync(HttpMethod.Delete, $"{Tenants}/{tenant:D}/members/{AccountOf(ada):D}", bob);

        banned.StatusCode.ShouldBe(HttpStatusCode.OK, await banned.ReadTextAsync());
        var orphaned = JsonNode.Parse(await banned.ReadTextAsync())!["orphanedTenants"]!.AsArray();
        orphaned.Count.ShouldBe(1);
        orphaned[0]!["id"]!.GetValue<Guid>().ShouldBe(tenant);
        orphaned[0]!["name"]!.GetValue<string>().ShouldBe("Acme");
        granted.StatusCode.ShouldBe(HttpStatusCode.OK, await granted.ReadTextAsync());
        var membership = JsonNode.Parse(await granted.ReadTextAsync())!;
        membership["tenantId"]!.GetValue<Guid>().ShouldBe(tenant);
        membership["kind"]!.GetValue<string>().ShouldBe("shared");
        membership["isOwner"]!.GetValue<bool>().ShouldBeTrue();
        membership["roles"]!.AsArray().Select(role => role!.GetValue<string>()).ShouldBe(["dispatcher"]);
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK, "the new owner manages the tenant");
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the new owner removes the banned one");
    }

    [Fact]
    public async Task Enabling_and_a_ban_that_orphans_nothing_answer_an_empty_list()
    {
        await using var world = await StartAsync();
        var (ada, _) = await world.SignInAsync("sub-ada");
        await CreatedAsync(world, ada, "Acme");
        var account = AccountOf(ada);

        (await BanAsync(world, account)).Dispose();
        using var enabled = await world.Api.SendAsync(
            HttpMethod.Put, AccountPath(account), AccountsWorld.Operator, body: new JsonObject { ["disabled"] = false });

        enabled.StatusCode.ShouldBe(HttpStatusCode.OK, await enabled.ReadTextAsync());
        JsonNode.Parse(await enabled.ReadTextAsync())!["orphanedTenants"]!.AsArray().ShouldBeEmpty("Ada manages her tenant again");
    }

    [Fact]
    public async Task A_grant_refuses_an_unknown_tenant_or_account_a_disabled_account_a_personal_tenant_and_a_foreign_role()
    {
        await using var world = await StartAsync();
        var (ada, _) = await world.SignInAsync("sub-ada");
        var (mallory, _) = await world.SignInAsync("sub-mallory");
        var tenant = await CreatedAsync(world, ada, "Acme");
        var personal = await world.PersonalTenantAsync(ada);
        (await BanAsync(world, AccountOf(mallory))).Dispose();

        using var noTenant = await GrantAsync(world, Guid.NewGuid(), AccountOf(ada));
        using var noAccount = await GrantAsync(world, tenant, Guid.NewGuid());
        using var disabled = await GrantAsync(world, tenant, AccountOf(mallory));
        using var personalTenant = await GrantAsync(world, personal.Value, AccountOf(ada));
        using var foreignRole = await GrantAsync(world, tenant, AccountOf(ada), new JsonObject { ["roles"] = new JsonArray("admin") });
        using var twice = await GrantAsync(world, tenant, AccountOf(ada), new JsonObject { ["roles"] = new JsonArray("dispatcher", "dispatcher") });

        await ProblemAsync(noTenant, HttpStatusCode.NotFound, AlvoProblemTypes.NotFound);
        await ProblemAsync(noAccount, HttpStatusCode.NotFound, AlvoProblemTypes.NotFound);
        await ProblemAsync(disabled, HttpStatusCode.Conflict, AlvoProblemTypes.Conflict);
        await ProblemAsync(personalTenant, HttpStatusCode.Conflict, AlvoProblemTypes.Conflict);
        await ProblemAsync(foreignRole, HttpStatusCode.UnprocessableEntity, AlvoProblemTypes.Validation);
        (await foreignRole.ReadTextAsync()).ShouldContain("dispatcher, technician", Case.Sensitive, "the refusal lists the assignable roles");
        await ProblemAsync(twice, HttpStatusCode.UnprocessableEntity, AlvoProblemTypes.Validation);
    }

    /// <summary>Until the audit component exists (#42), a grant leaves one Information line of ids — and a refusal none.</summary>
    [Fact]
    public async Task A_grant_is_logged_once_with_the_operator_the_account_and_the_tenant_only()
    {
        await using var world = await StartAsync();
        var (ada, _) = await world.SignInAsync("sub-ada", email: "ada@gmail.com");
        var (bob, _) = await world.SignInAsync("sub-bob", email: "bob@gmail.com");
        var tenant = await CreatedAsync(world, ada, "Acme");

        (await GrantAsync(world, tenant, AccountOf(bob))).Dispose();
        (await GrantAsync(world, Guid.NewGuid(), AccountOf(bob))).Dispose();

        var lines = world.Logs.Entries.Where(entry => entry.Line.Contains("an owner of tenant", StringComparison.Ordinal)).ToList();
        lines.Select(entry => entry.Level).ShouldBe([LogLevel.Information]);
        lines[0].Line.ShouldContain($"{AccountsWorld.Operator.KeyId}:{AccountsWorld.Operator.User} made end-user account {AccountOf(bob)} an owner of tenant {tenant}");
        lines.ShouldAllBe(entry => !entry.Line.Contains("bob@gmail.com", StringComparison.Ordinal));
    }

    private static Task<AccountsWorld> StartAsync() => AccountsWorld.StartAsync(_engine, descriptor: "team-tracker.alvo.json");

    private static async Task<Guid> CreatedAsync(AccountsWorld world, string access, string name)
    {
        using var created = await world.AsPersonAsync(HttpMethod.Post, Tenants, access, new JsonObject { ["name"] = name });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return JsonNode.Parse(await created.ReadTextAsync())!["tenantId"]!.GetValue<Guid>();
    }

    private static Task<HttpResponseMessage> BanAsync(AccountsWorld world, Guid account) => world.Api.SendAsync(
        HttpMethod.Put, AccountPath(account), AccountsWorld.Operator, body: new JsonObject { ["disabled"] = true });

    private static Task<HttpResponseMessage> GrantAsync(AccountsWorld world, Guid tenant, Guid account, JsonObject? body = null) =>
        world.Api.SendAsync(
            HttpMethod.Put,
            $"/management/projects/team-tracker/tenants/{tenant:D}/owners/{account:D}",
            AccountsWorld.Operator,
            body: body ?? []);

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        response.StatusCode.ShouldBe(status, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(type);
    }

    private static string AccountPath(Guid account) => $"/management/projects/team-tracker/accounts/{account:D}/disabled";

    private static Guid AccountOf(string access) => Guid.Parse(new JsonWebToken(access).Subject);
}
