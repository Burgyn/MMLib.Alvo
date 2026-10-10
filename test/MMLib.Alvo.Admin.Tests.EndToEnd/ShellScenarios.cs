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

        /* By its name, as a screen reader announces it, and modal: alvo.js holds the g-jumps while one is. */
        var palette = session.Page.GetByRole(AriaRole.Dialog, new() { Name = "Search" });
        await palette.WaitForAsync();
        (await palette.GetAttributeAsync("aria-modal")).ShouldBe("true");
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

        await session.Page.WaitForAddressAsync("**/admin/data");
        await palette.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }

    /// <summary>Ctrl+K opens it as ⌘K does, for a keyboard with no Command key.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Ctrl_K_opens_the_palette_too()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.Keyboard.PressAsync("Control+k");

        await session.Dialog("palette").WaitForAsync();
        await session.Page.WaitForFunctionAsync("document.activeElement?.dataset.testid === 'palette-input'");
        session.AssertConsoleClean();
    }

    /// <summary>Tab from the palette's input cycles inside the dialog; it never reaches the page behind it.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Tab_stays_inside_the_palette()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");
        await session.Page.Keyboard.PressAsync("Meta+k");
        await session.Page.WaitForFunctionAsync("document.activeElement?.dataset.testid === 'palette-input'");

        /* More presses than the palette has stops (the input and at most twelve rows), so the cycle wraps. The
           library's trap sends focus back from its edge over the circuit, so each press waits for focus to be
           inside again rather than reading it the instant the key is up. */
        for (var press = 0; press < 16; press += 1)
        {
            await session.Page.Keyboard.PressAsync("Tab");
            await session.Page.WaitForFunctionAsync(
                "() => !!document.activeElement?.closest('[role=dialog]') && !document.activeElement.matches('.pointer-events-none')",
                null, new() { Timeout = 5_000, PollingInterval = 50 });
        }
    }

    /// <summary>
    /// ⌘K over another dialog opens nothing (spec §3.1, never a dialog over a dialog), and ⌘K over the palette
    /// keeps what was typed in it.
    /// </summary>
    /// <remarks>Negatives, so they wait a fixed moment; the scenarios above show the same chord does open it.</remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_palette_never_stacks_and_a_second_chord_keeps_the_query()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("rename-entity").ClickAsync();
        await session.Page.GetByTestId("rename-sheet").WaitForAsync();

        await session.Page.Keyboard.PressAsync("Meta+k");
        await session.Page.WaitForTimeoutAsync(750);
        (await session.Page.GetByTestId("palette-input").CountAsync()).ShouldBe(0, "the palette opened over the sheet");

        await session.Page.Keyboard.PressAsync("Escape");
        await session.Page.GetByTestId("rename-sheet").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.Keyboard.PressAsync("Meta+k");
        var input = session.Page.GetByTestId("palette-input");
        await input.WaitForAsync();
        await session.Page.Keyboard.TypeAsync("acc");
        await session.Dialog("palette").GetByRole(AriaRole.Option, new() { Name = "Access", Selected = true }).WaitForAsync();

        await session.Page.Keyboard.PressAsync("Meta+k");
        await session.Page.WaitForTimeoutAsync(750);
        (await input.InputValueAsync()).ShouldBe("acc");
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

        await session.Page.WaitForAddressAsync("**/history");
        await sidebar.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await session.AssertNoHorizontalScrollAsync();
    }

    /// <summary>
    /// On a phone the drawer is the keyboard's too: opening it takes focus into it, and Escape closes it and gives
    /// focus back to Sections, whose aria-expanded says which it is throughout.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task On_a_phone_the_drawer_takes_focus_and_Escape_gives_it_back_to_Sections()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 390);
        await session.GoAsync("");
        var sections = session.Page.GetByTestId("more-sections");
        var sidebar = session.Page.GetByTestId("sidebar");

        await sections.FocusAsync();
        await session.Page.Keyboard.PressAsync("Enter");

        await sidebar.WaitForAsync();
        await session.Page.WaitForFunctionAsync("() => !!document.activeElement?.closest(\"[data-testid='sidebar']\")");
        (await sections.GetAttributeAsync("aria-expanded")).ShouldBe("true");

        await session.Page.Keyboard.PressAsync("Escape");

        await sidebar.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await session.Page.WaitForFunctionAsync("() => document.activeElement?.dataset.testid === 'more-sections'");
        (await sections.GetAttributeAsync("aria-expanded")).ShouldBe("false");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// A phone's first paint has no drawer over it: the server renders before the circuit knows the width, and what
    /// it rendered must not cover the page or push the content right while the circuit starts.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_phone_first_paint_has_no_drawer_over_the_page()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 390);
        await session.GoAsync("");

        await AdminSession.WithoutTheCircuitAsync(session.Page, () => session.Page.ReloadAsync());

        (await session.Page.GetByTestId("sidebar").IsVisibleAsync()).ShouldBeFalse("the drawer is drawn at first paint");
        var content = await session.Content.BoundingBoxAsync();
        content.ShouldNotBeNull();
        content.X.ShouldBeLessThanOrEqualTo(1, "the content is pushed right at first paint");
    }

    /// <summary>
    /// Between the library's small breakpoint (600 px) and Alvo's phone width (720 px) there is one navigation, the
    /// phone's: the bottom bar and Sections, with the drawer closed, never also a persistent sidebar.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task At_660_pixels_there_is_one_navigation_and_Sections_says_whether_it_is_open()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 660);
        await session.GoAsync("");
        var sections = session.Page.GetByTestId("more-sections");
        var sidebar = session.Page.GetByTestId("sidebar");

        (await session.Page.GetByTestId("bottom-nav").IsVisibleAsync()).ShouldBeTrue();
        (await sections.IsVisibleAsync()).ShouldBeTrue();
        (await sidebar.IsVisibleAsync()).ShouldBeFalse("a persistent sidebar beside the phone's bottom bar");
        (await sections.GetAttributeAsync("aria-expanded")).ShouldBe("false");

        await sections.ClickAsync();

        await sidebar.WaitForAsync();
        (await sections.GetAttributeAsync("aria-expanded")).ShouldBe("true");
        var content = await session.Content.BoundingBoxAsync();
        content.ShouldNotBeNull();
        content.X.ShouldBeLessThanOrEqualTo(1, "the drawer is over the page, not beside it");
        session.AssertConsoleClean();
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

        await session.Page.WaitForAddressAsync("**/admin/sign-in**");
    }

    /// <summary>
    /// The account menu is one stop on the way through the app bar, opens on Enter, reaches Sign out with the arrow,
    /// and Escape closes it with focus back on Account.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_account_menu_is_one_stop_and_works_from_the_keyboard()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");
        var account = session.Page.GetByTestId("appbar").GetByRole(AriaRole.Button, new() { Name = "Account", Exact = true });
        var signOut = session.Page.GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" });

        await session.Page.GetByTestId("theme-toggle").FocusAsync();
        await session.Page.Keyboard.PressAsync("Tab");
        (await IsFocused(account)).ShouldBeTrue($"Tab from the theme reached {await session.FocusedAsync()}");
        (await account.GetAttributeAsync("aria-haspopup")).ShouldBe("menu");
        (await account.GetAttributeAsync("aria-expanded")).ShouldBe("false");

        await session.Page.Keyboard.PressAsync("Enter");
        await signOut.WaitForAsync();
        (await account.GetAttributeAsync("aria-expanded")).ShouldBe("true");
        await session.Page.Keyboard.PressAsync("ArrowDown");
        await session.Page.WaitForFunctionAsync("() => document.activeElement?.getAttribute('role') === 'menuitem'");
        (await IsFocused(signOut)).ShouldBeTrue();

        await session.Page.Keyboard.PressAsync("Escape");
        await signOut.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        (await IsFocused(account)).ShouldBeTrue("focus returns to Account");

        /* One stop: the next Tab leaves the menu's own markup rather than landing on a wrapper around the button. */
        await session.Page.Keyboard.PressAsync("Tab");
        (await session.Page.EvaluateAsync<bool>(
            "() => !document.activeElement?.closest(\"[data-testid='account-menu']\")")).ShouldBeTrue();
        session.AssertConsoleClean();
    }

    /// <summary>Enter on Sign out posts the sign-out form, the same as a click.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Enter_on_Sign_out_signs_out()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.GetByTestId("appbar").GetByRole(AriaRole.Button, new() { Name = "Account", Exact = true }).FocusAsync();
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" }).WaitForAsync();
        await session.Page.Keyboard.PressAsync("ArrowDown");
        await session.Page.WaitForFunctionAsync("() => document.activeElement?.getAttribute('role') === 'menuitem'");
        await session.Page.Keyboard.PressAsync("Enter");

        await session.Page.WaitForAddressAsync("**/admin/sign-in**");
    }

    private static Task<bool> IsFocused(ILocator locator) => locator.EvaluateAsync<bool>("e => e === document.activeElement");

    /// <summary>The palette's row with <paramref name="name"/>, once it is the selected one.</summary>
    private static ILocator Selected(ILocator palette, string name)
        => palette.GetByRole(AriaRole.Option, new() { Name = name, Selected = true });
}
