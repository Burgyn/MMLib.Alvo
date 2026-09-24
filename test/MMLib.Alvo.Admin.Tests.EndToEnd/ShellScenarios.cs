using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// One app bar, one nav drawer, and a palette that is a real dialog (spec §3.7, §3.4; inventory defect #11).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class ShellScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_palette_is_a_dialog_that_takes_focus_and_gives_it_back_on_Escape()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        var search = session.Page.GetByTestId("appbar").GetByRole(AriaRole.Button, new() { Name = "Search" });

        await search.ClickAsync();

        var palette = session.Dialog("palette");
        await palette.WaitForAsync();
        (await palette.GetAttributeAsync("role")).ShouldBe("dialog");
        (await session.FocusedAsync()).ShouldContain("[palette-input]");

        await session.Page.Keyboard.PressAsync("Escape");
        await palette.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await search.EvaluateAsync<bool>("e => e === document.activeElement")).ShouldBeTrue("focus returns to the trigger");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// The palette is driven by the keyboard alone: ⌘K opens it over whatever held focus, the arrows move the
    /// selection, Escape gives focus back to that element, and Enter goes to the selected row.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_palette_is_driven_by_the_keyboard_and_gives_focus_back_to_where_it_was()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        var origin = session.Page.GetByTestId("sidebar").GetByRole(AriaRole.Link, new() { Name = "Rules", Exact = true });
        await origin.FocusAsync();

        await session.Page.Keyboard.PressAsync("Meta+k");
        var palette = session.Dialog("palette");
        await palette.WaitForAsync();
        await session.Page.WaitForFunctionAsync("document.activeElement?.dataset.testid === 'palette-input'");

        await session.Page.Keyboard.PressAsync("ArrowDown");
        await session.Page.Keyboard.PressAsync("ArrowDown");
        await Selected(palette, "Data").WaitForAsync();
        await session.Page.Keyboard.PressAsync("ArrowUp");
        await Selected(palette, "Schema").WaitForAsync();

        await session.Page.Keyboard.PressAsync("Escape");
        await palette.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await origin.EvaluateAsync<bool>("e => e === document.activeElement")).ShouldBeTrue("focus returns to where it was");

        await session.Page.Keyboard.PressAsync("Meta+k");
        await palette.WaitForAsync();
        await Selected(palette, "Overview").WaitForAsync();
        await session.Page.Keyboard.PressAsync("ArrowDown");
        await session.Page.Keyboard.PressAsync("ArrowDown");
        await Selected(palette, "Data").WaitForAsync();
        await session.Page.Keyboard.PressAsync("Enter");

        await session.Page.WaitForURLAsync("**/admin/data");
        await palette.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Tabbing_to_an_app_bar_button_shows_a_focus_ring()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");
        var toggle = session.Page.GetByTestId("theme-toggle");

        await toggle.FocusAsync();
        await session.Page.Keyboard.PressAsync("Shift+Tab");
        await session.Page.Keyboard.PressAsync("Tab");

        (await toggle.EvaluateAsync<bool>("e => e.matches(':focus-visible')")).ShouldBeTrue();
        (await toggle.EvaluateAsync<string>("e => getComputedStyle(e).boxShadow")).ShouldNotBe("none");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task On_a_phone_the_drawer_is_closed_until_asked_for_and_closes_on_a_choice()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 390);
        await session.GoAsync("");
        var sidebar = session.Page.GetByTestId("sidebar");
        (await sidebar.IsVisibleAsync()).ShouldBeFalse();
        (await session.Page.GetByTestId("bottom-nav").IsVisibleAsync()).ShouldBeTrue();

        await session.Page.GetByTestId("more-sections").ClickAsync();
        await sidebar.GetByRole(AriaRole.Link, new() { Name = "Configuration history", Exact = true }).ClickAsync();

        await session.Page.WaitForURLAsync("**/history");
        await sidebar.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await session.AssertNoHorizontalScrollAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task On_a_desktop_the_drawer_is_open_and_the_bottom_bar_is_gone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 1440);
        await session.GoAsync("");

        (await session.Page.GetByTestId("sidebar").IsVisibleAsync()).ShouldBeTrue();
        (await session.Page.GetByTestId("bottom-nav").IsVisibleAsync()).ShouldBeFalse();
        (await session.Page.GetByTestId("more-sections").IsVisibleAsync()).ShouldBeFalse();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_account_menu_says_who_is_signed_in_and_signs_them_out()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.GetByTestId("account-menu").ClickAsync();
        await session.Page.GetByText(AdminWorld.AdminEmail).First.WaitForAsync();
        await session.Page.GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" }).ClickAsync();

        await session.Page.WaitForURLAsync("**/admin/sign-in**");
    }

    /// <summary>The palette's row with <paramref name="name"/>, once it is the selected one.</summary>
    private static ILocator Selected(ILocator palette, string name)
        => palette.GetByRole(AriaRole.Option, new() { Name = name, Selected = true });
}
