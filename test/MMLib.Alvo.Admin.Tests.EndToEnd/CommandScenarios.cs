namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Commands on the dashboard (spec 2026-10-14 §7 HX-08): the Commands lane, and the simulator's verdict per command.</summary>
/// <param name="world">The field-service world with handlers and commands, and a browser.</param>
public sealed class CommandScenarios(ExtensionWorld world) : IClassFixture<ExtensionWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1400)]
    [InlineData(375)]
    public async Task The_lifecycle_tab_lists_a_command_bound_to_the_entity_with_its_route_and_access(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var item = session.Page.GetByTestId("lifecycle-commands").GetByTestId($"lane-command-{ExtensionWorld.Complete}");
        await item.WaitForAsync();

        (await item.GetByTestId("lane-command-route").InnerTextAsync()).Trim().ShouldBe("POST /commands/work-orders/{id:guid}/complete");
        (await item.GetByTestId("lane-command-access").InnerTextAsync()).Trim().ShouldBe("access 'dispatcher' in @user.roles && status == 'in_progress'");
        (await item.GetByRole(Microsoft.Playwright.AriaRole.Button).CountAsync()).ShouldBe(0, "host code is read-only on the dashboard");
        (await session.Page.GetByTestId("lifecycle-commands").GetByTestId($"lane-command-{ExtensionWorld.Notify}").CountAsync())
            .ShouldBe(0, "an internal command binds no row, so it is not on an entity's lane");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_simulator_answers_each_command_s_access_rule_and_scores_no_row()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");

        var commands = session.Page.GetByTestId("verdict-commands");
        await commands.WaitForAsync();

        (await commands.GetByTestId($"verdict-command-{ExtensionWorld.Complete}").GetByTestId("verdict-command-answer").InnerTextAsync())
            .Trim().ShouldBe("decided per row", "its rule reads the row's status");
        (await commands.GetByTestId($"verdict-command-{ExtensionWorld.Notify}").GetByTestId("verdict-command-answer").InnerTextAsync())
            .Trim().ShouldBeOneOf("admits", "refuses");
        (await session.Page.Locator("[data-record-verdict]").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
}
