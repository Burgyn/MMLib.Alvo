namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The Automations page lists the descriptor's rules, read-only, with no <c>Not yet</c> panel (HX-15).</summary>
/// <param name="world">The field-service world with one automation rule calling <c>issue-invoice</c>.</param>
public sealed class AutomationsScenarios(FunctionHookWorld world) : IClassFixture<FunctionHookWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_page_lists_the_seeded_rule_and_no_not_yet_panel()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/automations");

        var rule = session.Page.GetByTestId($"automation-{FunctionHookWorld.Rule}");
        await rule.WaitForAsync();
        (await rule.GetByTestId("automation-trigger").InnerTextAsync()).ShouldBe("When entity.regions.created");
        (await rule.GetByTestId("automation-action").InnerTextAsync()).ShouldContain("function — issue-invoice");
        (await session.Page.GetByTestId("not-yet-warned").CountAsync()).ShouldBe(0);
        (await session.Page.GetByTestId("not-yet-unknown").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_page_shows_the_wildcard_refusal_and_links_to_the_rule_s_deliveries()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/automations");

        await session.Page.GetByTestId("refused-trigger.event").WaitForAsync();
        await session.Page.GetByTestId($"automation-deliveries-{FunctionHookWorld.Rule}").ClickAsync();

        await session.Page.GetByTestId("ops-subscriber-filter").WaitForAsync();
        (await session.Page.GetByTestId("ops-subscriber-filter").InnerTextAsync()).ShouldContain($"/automation/{FunctionHookWorld.Rule}/actions#");
        session.AssertConsoleClean();
    }
}
