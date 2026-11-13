using Microsoft.Playwright;

using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// An entity's state machine on the dashboard (spec 2026-10-14 §7 HX-18 acceptance (4)): the States tab draws every state,
/// transition and timer, and the policy simulator answers whether a role may take each transition.
/// </summary>
/// <param name="world">The field-service world with a state machine on work orders, and a browser.</param>
public sealed class StateMachineScenarios(StateMachineWorld world) : IClassFixture<StateMachineWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1280)]
    [InlineData(390)]
    public async Task The_states_tab_draws_every_state_and_transition(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/schema/work_orders?tab=states");

        var machine = session.Page.GetByTestId("states-machine");
        await machine.WaitForAsync();
        // Five: every value of the status enum is a state, and field-service's enum gained `new`.
        (await machine.Locator("[data-testid^='sm-state-']").CountAsync()).ShouldBe(5);
        (await machine.Locator("[data-testid^='sm-transition-']").CountAsync()).ShouldBe(3);
        foreach (var transition in new[] { "start", "complete", "cancel" })
        {
            (await machine.GetByTestId($"sm-transition-{transition}").CountAsync()).ShouldBe(1);
            (await machine.GetByTestId($"sm-row-{transition}").CountAsync()).ShouldBe(1);
        }

        (await session.Page.GetByRole(AriaRole.Img, new() { Name = "State machine of work_orders" }).CountAsync()).ShouldBe(1);
        var timer = session.Page.GetByTestId("sm-timer-visit_reminder");
        (await timer.InnerTextAsync()).ShouldContain("scheduled_for - duration('2h')");
        (await timer.InnerTextAsync()).ShouldContain("work_orders.visit_reminder");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_without_a_machine_says_how_to_add_one()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers?tab=states");

        (await session.Page.GetByTestId("states-empty").InnerTextAsync()).ShouldContain("add stateMachine to its descriptor");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_simulator_answers_whether_a_role_may_take_a_transition()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");
        await PolicySimulation.ChooseOperationAsync(session, "update");
        var transitions = session.Page.GetByTestId("verdict-transitions");

        await ChooseCallerAsync(session, "technician");
        await AnswerAsync(transitions, "complete", "Allowed");
        await AnswerAsync(transitions, "cancel", "Not allowed");

        await ChooseCallerAsync(session, "dispatcher");
        await AnswerAsync(transitions, "cancel", "Depends on the record");
        await AnswerAsync(transitions, "complete", "Not allowed");
        (await transitions.GetByTestId("verdict-transition-cancel").GetByTestId("verdict-transition-when").InnerTextAsync())
            .ShouldContain("new.scheduled_for > now()");
        session.AssertConsoleClean();
    }

    private static async Task ChooseCallerAsync(AdminSession session, string caller)
    {
        var group = session.Page.GetByRole(AriaRole.Radiogroup, new() { Name = "Signed in as" });
        await group.GetByRole(AriaRole.Radio, new() { Name = caller, Exact = true }).ClickAsync();
        await group.GetByRole(AriaRole.Radio, new() { Name = caller, Exact = true, Checked = true }).WaitForAsync();
    }

    private static Task AnswerAsync(ILocator transitions, string transition, string answer) =>
        transitions.GetByTestId($"verdict-transition-{transition}").GetByTestId("verdict-transition-answer")
            .Filter(new() { HasTextRegex = new Regex($"^\\s*{Regex.Escape(answer)}\\s*$") })
            .WaitForAsync();
}
