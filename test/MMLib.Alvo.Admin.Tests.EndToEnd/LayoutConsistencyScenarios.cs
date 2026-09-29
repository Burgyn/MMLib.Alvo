using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// What the final screenshots found drawn two ways (S2, S3, S5): a section head's control on one line, a disclosure in
/// line with its panel's content, and an editor that reaches the window's edge, full width on a phone (spec §3.1).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class LayoutConsistencyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>Every section head's create stays on one line, the long subtitle beside it notwithstanding.</summary>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1400)]
    [InlineData(390)]
    public async Task A_section_heads_control_stays_on_one_line(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        /* By address, which a phone's scrolled tab strip does not stand in the way of. */
        foreach (var (tab, trigger) in new[] { ("fields", "New field"), ("indexes", "New index"), ("on-write", "New hook") })
        {
            await session.GoAsync($"/schema/work_orders?tab={tab}");
            var box = await AdminSession.SettledBoxAsync(session.Button(trigger, exact: true));
            box.Height.ShouldBeLessThan(44, $"{trigger} wrapped at {width} px");
        }
    }

    /// <summary>The hooks tab's fold of refused action types starts where the panel's head text does.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_disclosure_lines_up_with_its_panels_content()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        var panel = session.Page.GetByTestId("entity-tabpanel");
        var fold = await AdminSession.SettledBoxAsync(session.Page.GetByTestId("hooks-refused"));
        var title = await AdminSession.SettledBoxAsync(panel.GetByText("On write", new() { Exact = true }));
        fold.X.ShouldBe(title.X, 2, "the fold sits on the card's edge instead of under the head's text");
    }

    /// <summary>An editor is a side sheet to the window's right edge, and the whole width on a phone.</summary>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1400)]
    [InlineData(375)]
    public async Task An_editor_reaches_the_windows_edge(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/data/regions");
        await session.Page.GetByTestId("record-new").ClickAsync();

        var sheet = await AdminSession.SettledBoxAsync(session.Dialog("record-sheet"));
        (sheet.X + sheet.Width).ShouldBe(width, 1, "the sheet stops short of the window's right edge");
        if (width < 600)
        {
            sheet.X.ShouldBe(0, 1, "on a phone the sheet is the whole width");
        }
    }
}
