using Microsoft.Playwright;
using System.Text.RegularExpressions;

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
        (await item.GetByRole(AriaRole.Button).CountAsync()).ShouldBe(0, "host code is read-only on the dashboard");
        (await session.Page.GetByTestId("lifecycle-commands").GetByTestId($"lane-command-{ExtensionWorld.Notify}").CountAsync())
            .ShouldBe(0, "an internal command binds no row, so it is not on an entity's lane");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_no_command_acts_on_says_so_in_a_host_that_maps_commands()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions?tab=lifecycle");

        await session.Page.GetByTestId("lifecycle-commands").GetByTestId("lifecycle-commands-empty").WaitForAsync();
        (await session.Page.Locator("[data-testid^='lane-command-']").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1400)]
    [InlineData(375)]
    public async Task The_simulator_answers_each_command_s_access_rule_and_scores_no_row(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/rules/work_orders");

        var commands = session.Page.GetByTestId("verdict-commands");
        await commands.WaitForAsync();

        var complete = commands.GetByTestId($"verdict-command-{ExtensionWorld.Complete}");
        (await complete.GetByTestId("verdict-command-answer").InnerTextAsync())
            .Trim().ShouldBe("decided per row", "its rule reads the row's status");
        (await session.Page.Locator("[data-record-verdict]").CountAsync()).ShouldBe(0);
        await AssertStackedAsync(complete);
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_caller_only_rule_is_answered_for_the_chosen_caller_and_a_row_reading_one_stays_per_row()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");
        var commands = session.Page.GetByTestId("verdict-commands");
        await commands.WaitForAsync();

        await ChooseCallerAsync(session, "anonymous");
        await AnswerAsync(commands, ExtensionWorld.Notify, "refuses");
        await AnswerAsync(commands, ExtensionWorld.Complete, "decided per row");

        await ChooseCallerAsync(session, "dispatcher");
        await AnswerAsync(commands, ExtensionWorld.Notify, "admits");
        await AnswerAsync(commands, ExtensionWorld.Complete, "decided per row");
        session.AssertConsoleClean();
    }

    /// <summary>The name sits on one line beside its badge, and the rule is drawn below them — never squeezed beside them.</summary>
    private static async Task AssertStackedAsync(ILocator command)
    {
        var badge = await AdminSession.SettledBoxAsync(command.GetByTestId("verdict-command-answer"));
        var name = await AdminSession.SettledBoxAsync(command.GetByTestId("verdict-command-name"));
        var rule = await AdminSession.SettledBoxAsync(command.GetByTestId("verdict-command-access"));

        name.Height.ShouldBeLessThan(badge.Height * 1.5f, "the command's name is not broken mid-identifier");
        rule.Y.ShouldBeGreaterThanOrEqualTo(Math.Max(badge.Y + badge.Height, name.Y + name.Height) - 1, "the rule is on its own line");
    }

    private static async Task ChooseCallerAsync(AdminSession session, string caller)
    {
        var group = session.Page.GetByRole(AriaRole.Radiogroup, new() { Name = "Signed in as" });
        await group.GetByRole(AriaRole.Radio, new() { Name = caller, Exact = true }).ClickAsync();
        await group.GetByRole(AriaRole.Radio, new() { Name = caller, Exact = true, Checked = true }).WaitForAsync();
    }

    private static Task AnswerAsync(ILocator commands, string command, string answer) =>
        commands.GetByTestId($"verdict-command-{command}").GetByTestId("verdict-command-answer")
            .Filter(new() { HasTextRegex = new Regex($"^\\s*{Regex.Escape(answer)}\\s*$") })
            .WaitForAsync();
}

/// <summary>A host that maps no command never draws the Commands lane (HX-08 Ruling 57) — the container's case.</summary>
/// <param name="world">The shipped host as the container runs it, and a browser.</param>
public sealed class CommandLaneAbsenceScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_host_that_maps_no_command_draws_no_commands_lane()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        await session.Page.GetByTestId("lifecycle-after-commit").WaitForAsync();
        (await session.Page.GetByTestId("lifecycle-commands").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
}
