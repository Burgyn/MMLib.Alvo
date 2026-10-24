using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// An entity's tab strip draws its underline under the tab that is open, after a click on each (final screenshots S1:
/// after an in-page switch the underline stayed under the previous tab while the new tab's label was the active one).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class TabSliderScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_underline_follows_every_tab_that_is_opened()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        foreach (var name in new[] { "Relationships", "Rules", "On write", "Indexes", "API", "Fields", "Indexes", "On write" })
        {
            await session.OpenTabAsync(name);
            var tab = session.Page.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true, Selected = true });
            var slider = AdminSession.SliderOf(tab);
            await session.Page.WaitForFunctionAsync(
                """
                ([t, s]) => {
                  const a = t.getBoundingClientRect(), b = s.getBoundingClientRect();
                  return b.width > 0 && Math.abs((a.left + a.width / 2) - (b.left + b.width / 2)) < 4;
                }
                """,
                new object[] { await tab.ElementHandleAsync(), await slider.ElementHandleAsync() },
                new() { PollingInterval = 20, Timeout = 700 });
        }

        session.AssertConsoleClean();
    }

    /// <summary>
    /// On a desktop the whole entity strip is in view: no scroll arrows, and opening the last tab leaves the first one
    /// where a click reaches it.
    /// </summary>
    /// <remarks>
    /// The library's 160 px tab floor made seven tabs overflow a 1400 px desktop by eight pixels, so the strip grew
    /// arrows and opening API slid Fields out of sight behind them (HX-04, when Lifecycle became the seventh tab). A
    /// narrow desktop, 1024 px, so the next tab added is caught here before it reaches the default width.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Every_entity_tab_is_in_view_on_a_desktop()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 1024);
        await session.GoAsync("/schema/work_orders");

        await session.OpenTabAsync("API");
        (await session.Page.Locator("#entity-tabs .mud-tabs-scroll-button").CountAsync()).ShouldBe(0);
        await session.OpenTabAsync("Fields");

        session.AssertConsoleClean();
    }
}
