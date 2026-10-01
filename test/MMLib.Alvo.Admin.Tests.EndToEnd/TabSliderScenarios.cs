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
}
