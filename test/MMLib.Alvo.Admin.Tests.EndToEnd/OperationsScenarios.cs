using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Operating a handler from the dashboard (spec 2026-10-14 HX-06 acceptance (10)): pause → write → resume → delivered.</summary>
/// <param name="world">The field-service world with one region handler, and a browser.</param>
public sealed class OperationsScenarios(OperationsWorld world) : IClassFixture<OperationsWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_paused_handler_holds_its_delivery_until_it_is_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await world.SignInAsync(ct);
        await session.GoAsync("/operations");
        var handler = session.Page.GetByTestId($"ops-handler-{OperationsWorld.RegionSync}");
        await handler.WaitForAsync();

        var pause = handler.GetByTestId($"ops-pause-{OperationsWorld.RegionSync}");
        await pause.ClickAsync();
        var confirm = session.Dialog("ops-confirm");
        await confirm.GetByTestId("ops-confirm-cancel").ClickAsync();
        await session.FocusAfterConfirmAsync("ops-confirm", $"[data-testid='ops-pause-{OperationsWorld.RegionSync}']");

        await pause.ClickAsync();
        await confirm.GetByTestId("ops-reason").FillAsync("dispatch system down");
        await confirm.GetByTestId("ops-confirm-run").ClickAsync();
        await session.FocusAfterConfirmAsync("ops-confirm", $"[data-testid='ops-resume-{OperationsWorld.RegionSync}']");
        await Assertions.Expect(session.Page.GetByTestId($"ops-handler-paused-{OperationsWorld.RegionSync}")).ToBeVisibleAsync();

        await world.Services.GetRequiredService<IAlvoData>().CreateAsync(
            "regions", new Dictionary<string, object?> { ["code"] = "NORTH-9", ["name"] = "North nine" }, AlvoContext.System(null), cancellationToken: ct);
        var row = session.Page.GetByTestId("ops-delivery-list").Locator("[data-testid^='ops-row-']").First;
        await WaitForStatusAsync(session, row, "pending", "the paused handler's delivery was recorded within 15 seconds");
        world.Synced.ShouldNotContain("NORTH-9", "a paused handler runs nothing");

        await handler.GetByTestId($"ops-resume-{OperationsWorld.RegionSync}").ClickAsync();
        await Assertions.Expect(session.Page.GetByTestId($"ops-handler-paused-{OperationsWorld.RegionSync}")).ToHaveCountAsync(0);
        await WaitForStatusAsync(session, row, "succeeded", "the resumed handler ran within 15 seconds");

        world.Synced.ShouldContain("NORTH-9");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_page_fits_a_phone_and_names_the_scope_of_its_numbers()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 375);
        await session.GoAsync("/operations");
        var runs = session.Page.GetByTestId($"ops-runs-{OperationsWorld.RegionSync}");
        await runs.WaitForAsync();

        (await runs.InnerTextAsync()).ShouldContain("this instance");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    private static async Task WaitForStatusAsync(AdminSession session, ILocator row, string expected, string because)
    {
        for (var poll = 0; poll < 30; poll++)
        {
            if (await StatusAsync(row) == expected)
            {
                return;
            }

            await session.Page.WaitForTimeoutAsync(500);
            await session.Page.GetByTestId("ops-refresh").ClickAsync();
        }

        (await StatusAsync(row)).ShouldBe(expected, because);
    }

    private static async Task<string?> StatusAsync(ILocator row) =>
        await row.CountAsync() == 0 ? null : (await row.GetByTestId("ops-delivery-status").InnerTextAsync()).Trim();
}
