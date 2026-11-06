using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;
using MMLib.Alvo.Reads;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The embedded policy simulator lists a narrowing among what runs on a read of its entity, and never on a write.</summary>
public sealed class ReadExtensionSimulatorTests
{
    private const string Project = "reads-desk";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_simulated_list_lists_the_narrowing()
    {
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder
            .FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "reads-desk.alvo.json"))
            .Entity("notes", n => n.Narrow("north-only", (QueryNarrowing _) => new AlvoComparison("region", AlvoFilterOperator.Eq, "north"))),
            cancellationToken: Ct);

        var list = await SimulateAsync(alvo, "list");
        var create = await SimulateAsync(alvo, "create");

        list.Extensions.ShouldHaveSingleItem().Id.ShouldBe("north-only");
        create.Extensions.ShouldBeEmpty();
    }

    private static async Task<ManagementPolicyVerdict> SimulateAsync(AlvoTestHost alvo, string operation)
    {
        using var scope = alvo.Services.CreateScope();
        var auditor = AlvoTestHost.Caller("auditor");
        scope.ServiceProvider.GetRequiredService<IAlvoContextAccessor>().Principal =
            new AlvoPrincipal { Context = auditor, Scopes = new HashSet<ApiKeyScope>(), KeyId = "reads-desk" };

        return await scope.ServiceProvider.GetRequiredService<IAlvoManagement>().SimulatePolicyAsync(
            Project, new ManagementPolicySimulation("notes", operation, new ManagementSimulatedCaller(auditor.User.Value, ["clerk"])), Ct);
    }
}
