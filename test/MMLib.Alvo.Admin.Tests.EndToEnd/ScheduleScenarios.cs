using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A schedule on the Operations page: its cron, run now, its last outcome, and the timer counts beside it.</summary>
/// <param name="world">The field-service world with the nightly schedule, and a browser.</param>
public sealed class ScheduleScenarios(OperationsWorld world) : IClassFixture<OperationsWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_schedule_runs_now_and_shows_its_last_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await world.Services.GetRequiredService<IAlvoScheduler>().ScheduleAsync(
            "e2e-reminder", DateTimeOffset.UtcNow.AddDays(1), "e2e.reminder_due", data: null, TenantId.New(), ct);
        await using var session = await world.SignInAsync(ct);
        await session.GoAsync("/operations");

        var row = session.Page.GetByTestId($"ops-job-{OperationsWorld.NightlyRoll}");
        await Assertions.Expect(row).ToContainTextAsync("0 2 * * *");
        await Assertions.Expect(row).ToContainTextAsync("Europe/Bratislava");
        await Assertions.Expect(session.Page.GetByTestId("timer-counts")).ToContainTextAsync("1 pending");

        await row.GetByTestId($"ops-run-now-{OperationsWorld.NightlyRoll}").ClickAsync();
        await session.Dialog("ops-confirm").GetByTestId("ops-confirm-run").ClickAsync();
        await Assertions.Expect(session.Page.GetByRole(AriaRole.Status)).ToContainTextAsync($"{OperationsWorld.NightlyRoll} will run");
        await WaitForOutcomeAsync(session, "succeeded");

        OperationsWorld.RollSlots.Runs.ShouldBe(1, "run now ran the job once, and its 02:00 occurrence has not come");
        session.AssertConsoleClean();
    }

    private static async Task WaitForOutcomeAsync(AdminSession session, string expected)
    {
        var outcome = session.Page.GetByTestId($"ops-job-outcome-{OperationsWorld.NightlyRoll}");
        for (var poll = 0; poll < 30 && !await HasOutcomeAsync(outcome, expected); poll++)
        {
            await session.Page.WaitForTimeoutAsync(500);
            await session.Page.GetByTestId("ops-refresh").ClickAsync();
        }

        await Assertions.Expect(outcome).ToHaveTextAsync(expected);
    }

    private static async Task<bool> HasOutcomeAsync(ILocator outcome, string expected) =>
        await outcome.CountAsync() > 0 && (await outcome.InnerTextAsync()).Trim() == expected;
}
