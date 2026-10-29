using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The extension catalog says which handlers an operator paused (spec 2026-10-14 §4.13 <c>Paused</c>), so every reader of it — the dashboard, the assistant — sees it.</summary>
public sealed class PausedCatalogTests
{
    [Fact]
    public async Task A_paused_handler_is_listed_paused_and_a_resumed_one_is_not()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new OperationsTestWorld();
        var management = world.Services.GetRequiredService<IAlvoManagement>();

        await world.Service.SetPausedAsync(OperationsTestWorld.Project, OperationsTestWorld.Handler, paused: true, reason: null, ct);
        (await management.GetExtensionsAsync(OperationsTestWorld.Project, ct)).Items.ShouldHaveSingleItem().Paused.ShouldBeTrue();

        await world.Service.SetPausedAsync(OperationsTestWorld.Project, OperationsTestWorld.Handler, paused: false, reason: null, ct);
        (await management.GetExtensionsAsync(OperationsTestWorld.Project, ct)).Items.ShouldHaveSingleItem().Paused.ShouldBeFalse();
    }
}
