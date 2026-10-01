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
        var pending = await PendingTextAsync(session);

        await session.Page.FillAsync("#rule-list", "'amdin' in @user.roles");
        var finding = session.Page.GetByTestId("check-rule-list");
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("amdin");
        await ShouldKeepFocusAsync(session, "rule-list");
        (await PendingTextAsync(session)).ShouldBe(pending, "a check never dirties the working copy");

        await session.Page.FillAsync("#rule-list", "'admin' in @user.roles");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session, "rule-list");
        await session.Page.GetByTestId("rule-dirty-list").WaitForAsync();
        (await PendingTextAsync(session)).ShouldBe(pending, "nor does clearing one");
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

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_condition_naming_an_undeclared_field_is_flagged_and_the_flag_clears_when_it_is_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("On write");
        await session.Page.GetByTestId("hook-new").ClickAsync();

        await session.Page.FillAsync("#hook-condition", "new.no_such_field == 'priority'");
        var finding = session.Page.GetByTestId("check-hook-condition");
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("no_such_field");
        await ShouldKeepFocusAsync(session, "hook-condition");

        await session.Page.FillAsync("#hook-condition", "new.tier == 'priority'");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session, "hook-condition");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_mutate_value_naming_an_undeclared_field_is_flagged_and_the_flag_clears_when_it_is_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("On write");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = "mutate", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#hook-mutate-field", "name");

        await session.Page.FillAsync("#hook-mutate-value", "new.no_such_field");
        var finding = session.Page.GetByTestId("check-hook-mutate-value").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("no_such_field");
        await ShouldKeepFocusAsync(session, "hook-mutate-value");

        await session.Page.FillAsync("#hook-mutate-value", "new.code");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        (await session.Page.GetByTestId("check-hook-mutate-value").CountAsync()).ShouldBe(0, "every sentence goes, not only the first");
        await ShouldKeepFocusAsync(session, "hook-mutate-value");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_computed_expression_naming_an_undeclared_field_is_flagged_and_the_flag_clears_when_it_is_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("double_price");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "decimal", Exact = true }).ClickAsync();

        await session.Page.FillAsync("#new-field-computed", "no_such_field * 2");
        var finding = session.Page.GetByTestId("check-new-field-computed").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("no_such_field");
        await ShouldKeepFocusAsync(session, "new-field-computed");

        await session.Page.FillAsync("#new-field-computed", "quoted_price * quoted_price");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session, "new-field-computed");
    }

    /// <summary>What the pending bar says; a check stages nothing, so it must not move.</summary>
    private static async Task<string> PendingTextAsync(AdminSession session)
    {
        var bar = session.Page.GetByTestId("pending-bar");
        return await bar.CountAsync() == 0 ? string.Empty : await bar.InnerTextAsync();
    }

    private static async Task ShouldKeepFocusAsync(AdminSession session, string id)
        => (await session.Page.EvaluateAsync<string>("document.activeElement?.id ?? ''")).ShouldBe(id);
}
