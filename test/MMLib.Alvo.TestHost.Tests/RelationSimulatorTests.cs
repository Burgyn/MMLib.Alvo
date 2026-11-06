using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The policy simulator names every relation a decision reads through, and the related scope it was bound to.</summary>
public sealed class RelationSimulatorTests
{
    private const string Project = "relations-sim";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Simulate_names_each_relation_its_bound_list_rule_and_a_denied_one()
    {
        await using var alvo = await StartAsync();

        var verdict = await SimulateAsync(alvo, "orders", "list", ["clerk"]);

        verdict.Relations.ShouldBe(
        [
            new ManagementRelationVerdict("customer_id", "customers", true, "owner_id == @user.id", null),
            new ManagementRelationVerdict("region_id", "regions", false, null, null),
        ]);
    }

    [Fact]
    public async Task A_reverse_relation_is_named_by_its_child_and_the_child_s_reference()
    {
        await using var alvo = await StartAsync();

        var verdict = await SimulateAsync(alvo, "projects", "list", ["clerk"]);

        verdict.Relations.ShouldHaveSingleItem().ShouldBe(
            new ManagementRelationVerdict("project_members.project_id", "project_members", true, "user_id == @user.id", null));
    }

    [Fact]
    public async Task A_lookup_check_is_named_on_a_create_and_a_decision_with_no_relation_names_none()
    {
        await using var alvo = await StartAsync();

        var create = await SimulateAsync(alvo, "orders", "create", ["clerk"]);
        var plain = await SimulateAsync(alvo, "customers", "list", ["clerk"]);

        create.Relations.ShouldHaveSingleItem().Via.ShouldBe("customer_id");
        plain.Relations.ShouldBeEmpty();
    }

    private static Task<AlvoTestHost> StartAsync() => AlvoTestHost.StartAsync(
        builder => builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "relations-sim.alvo.json")),
        cancellationToken: Ct);

    private static async Task<ManagementPolicyVerdict> SimulateAsync(
        AlvoTestHost alvo, string entity, string operation, IReadOnlyList<string> roles)
    {
        using var scope = alvo.Services.CreateScope();
        var auditor = AlvoTestHost.Caller("auditor");
        scope.ServiceProvider.GetRequiredService<IAlvoContextAccessor>().Principal =
            new AlvoPrincipal { Context = auditor, Scopes = new HashSet<ApiKeyScope>(), KeyId = Project };

        return await scope.ServiceProvider.GetRequiredService<IAlvoManagement>().SimulatePolicyAsync(
            Project, new ManagementPolicySimulation(entity, operation, new ManagementSimulatedCaller(Guid.NewGuid(), roles)), Ct);
    }
}
