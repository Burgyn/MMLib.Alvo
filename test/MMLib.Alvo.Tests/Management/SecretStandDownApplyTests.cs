using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// A dashboard-first boot that stood down for a required secret (HX-15 ruling Q6) refuses an apply and a rollback until the
/// host restarts: either would prime the catalog and serve routes without the secret (HX-15 review m3).
/// </summary>
public sealed class SecretStandDownApplyTests
{
    private const string Reason = "Required secret 'erp-token' is not in the secret store.";

    [Fact]
    public async Task An_apply_during_a_required_secret_stand_down_is_refused_naming_the_secret()
    {
        using var world = StoodDown();

        var refusal = await Should.ThrowAsync<ManagementConflictException>(() => Management(world).ApplyDescriptorAsync(
            OperationsTestWorld.Project, new ManagementApplyRequest("{}", ExpectedRevision: 1), OperationsTestWorld.Ct));

        refusal.Message.ShouldStartWith(Reason);
        refusal.Message.ShouldContain("restart the host");
    }

    [Fact]
    public async Task A_rollback_during_a_required_secret_stand_down_is_refused()
    {
        using var world = StoodDown();

        var refusal = await Should.ThrowAsync<ManagementConflictException>(() => Management(world).RollbackAsync(
            OperationsTestWorld.Project, 1, new ManagementRollbackRequest(ExpectedRevision: 1), OperationsTestWorld.Ct));

        refusal.Message.ShouldStartWith(Reason);
    }

    private static OperationsTestWorld StoodDown()
    {
        var world = new OperationsTestWorld();
        world.Services.GetRequiredService<AlvoBootState>().StoodDownForSecrets(OperationsTestWorld.Project, Reason);
        return world;
    }

    private static IAlvoManagement Management(OperationsTestWorld world) => world.Services.GetRequiredService<IAlvoManagement>();
}
