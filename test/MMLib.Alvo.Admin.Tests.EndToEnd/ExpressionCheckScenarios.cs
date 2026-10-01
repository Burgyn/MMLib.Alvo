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
        await ShouldKeepFocusAsync(session);

        await session.Page.FillAsync("#rule-list", "'admin' in @user.roles");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session);
        (await session.Page.GetByTestId("pending-bar").CountAsync()).ShouldBe(0, "a check stages nothing");
    }

    private static async Task ShouldKeepFocusAsync(AdminSession session)
        => (await session.Page.EvaluateAsync<string>("document.activeElement?.id ?? ''")).ShouldBe("rule-list");
}
