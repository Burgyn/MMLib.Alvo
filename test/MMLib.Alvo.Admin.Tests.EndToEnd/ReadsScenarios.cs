using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Read extensions on the dashboard (spec 2026-10-14 §4.13, HX-16): the Reads lane and the simulator's code list.</summary>
/// <param name="world">The field-service world with a narrowing and an enricher, and a browser.</param>
public sealed class ReadsScenarios(ReadsWorld world) : IClassFixture<ReadsWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1280)]
    [InlineData(390)]
    public async Task The_lifecycle_tab_draws_the_reads_lane_read_only(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var reads = session.Page.GetByTestId("lifecycle-reads");
        await reads.WaitForAsync();
        (await reads.GetByTestId($"lane-extension-{ReadsWorld.Narrowing}").GetByTestId("lane-extension-stage").InnerTextAsync()).Trim().ShouldBe("narrows reads");
        (await reads.GetByTestId($"lane-extension-{ReadsWorld.Enricher}").GetByTestId("lane-extension-stage").InnerTextAsync()).Trim().ShouldBe("shapes reads");
        (await reads.GetByTestId("lifecycle-reads-empty").CountAsync()).ShouldBe(0);
        (await reads.GetByRole(AriaRole.Button).CountAsync()).ShouldBe(0, "host code is read-only on the dashboard");
        (await session.Page.GetByTestId("lifecycle-after-commit").GetByTestId($"lane-extension-{ReadsWorld.Narrowing}").CountAsync())
            .ShouldBe(0, "a read extension runs on no write, so it is not drawn as one");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_simulator_lists_the_narrowing_on_a_simulated_list()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");

        await PolicySimulation.ChooseOperationAsync(session, "list");

        var narrowing = session.Page.GetByTestId("verdict-extensions").GetByTestId($"verdict-extension-{ReadsWorld.Narrowing}");
        await narrowing.WaitForAsync();
        (await narrowing.GetByTestId("verdict-extension-stage").InnerTextAsync()).Trim().ShouldBe("narrows reads");
        session.AssertConsoleClean();
    }
}

/// <summary>A host with handlers and commands but no read extension draws no Reads lane (Ruling 29's rule).</summary>
/// <param name="world">The extension world.</param>
public sealed class ReadsLaneAbsenceScenarios(ExtensionWorld world) : IClassFixture<ExtensionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_host_with_no_read_extension_draws_no_reads_lane()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");
        await session.Page.GetByTestId("lifecycle-after-commit").WaitForAsync();

        (await session.Page.GetByTestId("lifecycle-reads").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
}
