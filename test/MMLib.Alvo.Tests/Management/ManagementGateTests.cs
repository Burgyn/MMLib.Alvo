using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The operations port's gate is the management surface's own: the level first, then the project.</summary>
public sealed class ManagementGateTests
{
    [Fact]
    public void An_unpublished_caller_is_refused_before_the_project_is_looked_at()
    {
        using var world = new OperationsTestWorld(admitted: false);
        var gate = world.Services.GetRequiredService<ManagementGate>();

        Should.Throw<ManagementForbiddenException>(() => gate.Admit(ManagementOperation.ListDeliveries, "no-such-project"));
    }

    [Fact]
    public void An_admitted_caller_naming_a_project_this_instance_does_not_serve_is_told_which_it_serves()
    {
        using var world = new OperationsTestWorld();
        var gate = world.Services.GetRequiredService<ManagementGate>();

        Should.Throw<ManagementProjectNotFoundException>(() => gate.Admit(ManagementOperation.ListDeliveries, "no-such-project"))
            .Message.ShouldContain(OperationsTestWorld.Project);
    }

    [Fact]
    public void The_actor_names_the_credential_and_the_user()
    {
        using var world = new OperationsTestWorld();

        world.Services.GetRequiredService<ManagementGate>().Actor.ShouldBe($"ops-key:{ManagementCallers.Bootstrap.Value}");
    }
}
