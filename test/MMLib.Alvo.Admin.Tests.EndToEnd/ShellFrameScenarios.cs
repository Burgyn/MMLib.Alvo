using Microsoft.Playwright;
using System.Globalization;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The shell is an application frame: the content pane scrolls and the chrome around it does not, and a
/// split's reading pane is as wide as the operator makes it.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class ShellFrameScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// Scrolled to the bottom of a screen taller than the window, the header, the brand and who is signed in
    /// are all still on screen, and the document itself never scrolled.
    /// </summary>
    /// <remarks>
    /// <b>The defect this pins</b>: the shell was <c>min-height: 100vh</c>, so the document scrolled and the
    /// sidebar and header went with it — the search, the project card and the sign-out were exactly what
    /// scrolled away on a long screen.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_chrome_stays_put_while_the_content_scrolls()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.SetViewportSizeAsync(1400, 480);
        await session.GoAsync("/rules");

        var scrolled = await session.Page.EvaluateAsync<int>(
            "() => { const pane = document.querySelector('main'); pane.scrollTop = pane.scrollHeight; return pane.scrollTop; }");
        scrolled.ShouldBeGreaterThan(0, "the screen has to be taller than the window for this to mean anything");

        (await session.Page.EvaluateAsync<int>("() => document.scrollingElement.scrollTop")).ShouldBe(0);
        await AssertInViewAsync(session.Button("Search"));
        await AssertInViewAsync(session.Page.GetByTestId("sidebar").GetByRole(AriaRole.Link, new() { Name = "Alvo" }));
        await AssertInViewAsync(session.Page.GetByTestId("sidebar").GetByText(AdminWorld.AdminEmail));

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The handle between the rules and the simulator resizes the simulator from the keyboard, the width
    /// survives a reload, and a double-click gives it back.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_reading_pane_is_resized_remembered_and_reset()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules");
        var handle = session.Page.GetByRole(AriaRole.Separator, new() { Name = "Resize the side panel" });

        await handle.FocusAsync();
        var before = await WidthAsync(handle);
        await handle.PressAsync("ArrowLeft");
        await handle.PressAsync("ArrowLeft");
        var wider = await WidthAsync(handle);
        wider.ShouldBe(before + 48, 1);

        await session.GoAsync("/rules");
        await handle.FocusAsync();
        (await WidthAsync(handle)).ShouldBe(wider, 1);

        await handle.DblClickAsync();
        (await WidthAsync(handle)).ShouldBe(before, 1);

        session.AssertConsoleClean();
    }

    private static async Task<double> WidthAsync(ILocator handle)
        => double.Parse(await handle.GetAttributeAsync("aria-valuenow") ?? "0", CultureInfo.InvariantCulture);

    private static async Task AssertInViewAsync(ILocator locator)
    {
        var box = await locator.BoundingBoxAsync();
        box.ShouldNotBeNull();
        var height = await locator.Page.EvaluateAsync<int>("() => innerHeight");
        box.Y.ShouldBeGreaterThanOrEqualTo(0);
        (box.Y + box.Height).ShouldBeLessThanOrEqualTo(height);
    }
}
