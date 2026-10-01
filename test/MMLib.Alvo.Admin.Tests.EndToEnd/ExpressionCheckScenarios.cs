using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>What apply would say about an expression is shown under its input while it is typed, without taking focus.</summary>
/// <param name="world">The running host and browser.</param>
public sealed class ExpressionCheckScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_rule_naming_an_undeclared_role_is_flagged_and_the_flag_clears_when_it_is_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("Rules");

        await session.Page.FillAsync("#rule-list", "'amdin' in @user.roles");
        var finding = session.Page.GetByTestId("check-rule-list");
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("amdin");
        await ShouldKeepFocusAsync(session, "rule-list");

        await session.Page.FillAsync("#rule-list", "'admin' in @user.roles");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session, "rule-list");
        await session.Page.GetByTestId("rule-dirty-list").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Escape_puts_back_the_saved_rule_and_the_flag_for_the_discarded_draft_goes()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("Rules");
        var declared = await session.Page.InputValueAsync("#rule-get");

        await session.Page.FillAsync("#rule-get", "'amdin' in @user.roles");
        var finding = session.Page.GetByTestId("check-rule-get");
        await finding.WaitForAsync(new() { Timeout = 3_000 });

        await session.Page.Keyboard.PressAsync("Escape");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        (await session.Page.InputValueAsync("#rule-get")).ShouldBe(declared);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_flagged_rule_can_still_be_saved()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("Rules");

        await session.Page.FillAsync("#rule-update", "'amdin' in @user.roles");
        await session.Page.GetByTestId("check-rule-update").WaitForAsync(new() { Timeout = 3_000 });
        var save = session.Page.GetByTestId("rule-save-update");
        (await save.IsEnabledAsync()).ShouldBeTrue("a check never gates Save");

        await save.ClickAsync();

        await session.SnackbarAsync("Rule saved to the working copy");
        await session.Page.GetByTestId("pending-bar").WaitForAsync();
    }

    private static async Task ShouldKeepFocusAsync(AdminSession session, string id)
        => (await session.Page.EvaluateAsync<string>("document.activeElement?.id ?? ''")).ShouldBe(id);
}
