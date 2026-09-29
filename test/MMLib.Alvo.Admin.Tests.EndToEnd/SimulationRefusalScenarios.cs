using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A host whose policy simulation can be made to fail, the way a store that is briefly unreachable does.</summary>
public sealed class FailingSimulationWorld : AdminWorld
{
    /// <summary>Whether every simulation throws.</summary>
    public bool FailSimulations { get; set; }

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
        => ManagementDecorator.Around(services, shipped => new Failing(shipped, this));

    private sealed class Failing(IAlvoManagement inner, FailingSimulationWorld world) : ManagementDecorator(inner)
    {
        public override Task<ManagementPolicyVerdict> SimulatePolicyAsync(
            string project, ManagementPolicySimulation simulation, CancellationToken ct = default)
            => world.FailSimulations
                ? throw new InvalidOperationException("The policy engine did not answer.")
                : base.SimulatePolicyAsync(project, simulation, ct);
    }
}

/// <summary>
/// A simulation the operator asked for that fails is a refusal that takes focus, and a second one takes it again (spec
/// §3.3; batch-B re-review N3: it was drawn outside RefusalState, so a second failure neither refocused nor re-read).
/// </summary>
/// <param name="world">A host whose simulations can be made to fail.</param>
public sealed class SimulationRefusalScenarios(FailingSimulationWorld world) : IClassFixture<FailingSimulationWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Every_refused_simulation_takes_focus_again()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");
        world.FailSimulations = true;
        try
        {
            foreach (var operation in new[] { "get", "create" })
            {
                await session.Page.GetByRole(AriaRole.Radio, new() { Name = operation, Exact = true }).ClickAsync();
                await session.WaitForFocusInsideAsync("error-panel");
                await session.Page.GetByRole(AriaRole.Radio, new() { Name = operation, Exact = true }).FocusAsync();
            }

            (await session.SnackbarCountAsync()).ShouldBe(0, "no error is ever a snackbar");
        }
        finally
        {
            world.FailSimulations = false;
        }
    }
}
