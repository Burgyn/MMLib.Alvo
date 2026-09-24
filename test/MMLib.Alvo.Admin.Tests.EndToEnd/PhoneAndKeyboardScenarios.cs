using System.Globalization;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The two acceptance criteria that a screenshot cannot answer.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class PhoneAndKeyboardScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly string[] _routes =
        ["", "/schema", "/schema/work_orders", "/data", "/data/regions", "/rules",
         "/access", "/history", "/integrations", "/settings", "/automations", "/functions"];

    /// <summary>
    /// Every screen is operable at 375 px with no horizontal scroll.
    /// </summary>
    /// <remarks>
    /// F5's first acceptance criterion, measured rather than eyeballed — and measured on every
    /// route rather than on the two somebody remembers, because the one that scrolls is always the
    /// one nobody opened on a phone.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Every_screen_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);

        foreach (var route in _routes)
        {
            await session.GoAsync(route);
            await session.AssertNoHorizontalScrollAsync();
            await session.AssertNoVerticalTextAsync();
            await session.AssertRenderedAsync();
        }

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A screen's secondary actions fold behind the overflow menu on a phone, and are reachable there.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, because either one alone is the bug.</b> Hidden and unreachable is a control that
    /// vanished at 375 px; shown inline is the wall of equal-weight buttons this exists to undo. Measured on
    /// the entity screen because it is the one that gains a control with every editor the dashboard grows.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Secondary_actions_fold_into_the_overflow_menu_on_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/schema/work_orders");

        var inline = session.Page.Locator(".a-pagehead__secondary a:has-text('Browse records')");
        await inline.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden });

        await session.Page.ClickAsync("[data-testid='pagehead-overflow']");
        await session.Page.Locator(
            "[data-testid='pagehead-overflow-sheet'] a:has-text('Browse records')").WaitForAsync();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// On a desktop they stay on the row, and the overflow button is not drawn over them.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Secondary_actions_stay_on_the_row_on_a_desktop()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.Locator(".a-pagehead__secondary a:has-text('Browse records')").WaitForAsync();
        (await session.Page.Locator("[data-testid='pagehead-overflow']").IsVisibleAsync()).ShouldBeFalse();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Rows are visible at both widths, in whichever layout that width draws.
    /// </summary>
    /// <remarks>
    /// <b>The defect this exists for shipped, and this suite was green while it did.</b> The stylesheet
    /// hides the table below 720 px and the row cards above it, and the Razor dashboard had only the
    /// table — so a phone showed a row count over an empty box. Asserting "a row is visible" rather than
    /// "a <c>&lt;td&gt;</c> is attached" is the whole difference: the second passes for a layout nobody
    /// can see.
    /// </remarks>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1280)]
    [InlineData(375)]
    public async Task A_data_screen_shows_its_rows_at_this_width(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/data/regions");

        /* The row this fact reads is the one it writes: the example ships no seed rows, and a fact that
           asserted over an empty table would pass for both the fixed layout and the broken one. */
        await session.Page.ClickAsync("button:has-text('New record')");
        await session.Page.Locator("#rf-name").WaitForAsync();
        await session.Page.FillAsync("#rf-name", $"Visible at {width.ToString(CultureInfo.InvariantCulture)}");
        await session.Page.FillAsync("#rf-code", width <= 720 ? "PHN" : "WDE");
        await session.Page.ClickAsync("button:has-text('Create')");

        var rows = width <= 720
            ? session.Page.Locator("[data-testid='row-card']")
            : session.Page.Locator("table.a-grid tbody tr");

        await rows.First.WaitForAsync();
        (await rows.CountAsync()).ShouldBeGreaterThan(0);
        (await rows.First.InnerTextAsync()).ShouldContain("Visible at");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Every screen renders on a desktop too, and none of them throws.
    /// </summary>
    /// <remarks>
    /// The same sweep at 1400 px. It is the cheapest test in the suite and it is the one that
    /// catches a screen somebody broke while working on another: a route that throws renders the
    /// chrome and an empty content area, which passes every check but this one.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Every_screen_renders_on_a_desktop()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);

        foreach (var route in _routes)
        {
            await session.GoAsync(route);
            await session.AssertRenderedAsync();
        }

        session.AssertConsoleClean();
    }

    /// <summary>
    /// At phone width the bar sits under the content rather than beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the assertion "no horizontal scroll" cannot make.</b> The bar is a sibling of the
    /// main column inside a row-direction shell, so showing it without stacking the shell laid it
    /// out as a second column: the content kept to about half the viewport, wrapped one word per
    /// line, and the page never overflowed — so every existing check passed and the dashboard was
    /// unusable on a phone.
    /// </para>
    /// <para>
    /// The measurement is therefore geometric rather than visual: the content fills the viewport,
    /// and the bar begins below where the content ends.
    /// </para>
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_phone_bar_sits_under_the_content_not_beside_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("");

        var content = await session.Page.Locator("main.a-content").BoundingBoxAsync();
        var bar = await session.Page.Locator("nav.a-bottomnav").BoundingBoxAsync();

        content.ShouldNotBeNull();
        bar.ShouldNotBeNull();
        content!.Width.ShouldBeGreaterThan(340);
        bar!.Width.ShouldBeGreaterThan(340);
        content.X.ShouldBeLessThanOrEqualTo(1);
        bar.X.ShouldBeLessThanOrEqualTo(1);

        /* Not "the bar begins where the content ends": the bar is sticky, so on a screen whose
           content scrolls it is pinned to the viewport's bottom edge and sits visually over the
           content's own box. Starting below the content's top is what distinguishes a row from a
           column, and it is all this can honestly assert. */
        bar.Y.ShouldBeGreaterThan(content.Y);

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Every section the sidebar offers is reachable from a phone, and so is signing out.
    /// </summary>
    /// <remarks>
    /// The bar carries five entries and the sidebar is hidden under 720 px, so without the sheet
    /// the phone reaches neither Configuration history, Integrations, Settings, the two "not yet"
    /// sections, nor the way out. <c>AdminNavigation</c>'s own remarks already claimed those live
    /// <em>"in the sidebar and in the sheet"</em> while the sheet did not exist — which is why the
    /// claim is now a test rather than a sentence.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Every_section_is_reachable_from_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("");

        await session.Page.Locator("[data-testid='more-sections']").ClickAsync();
        var sheet = session.Page.Locator("[data-testid='sections-sheet']");
        await sheet.WaitForAsync();

        foreach (var route in new[] { "/history", "/integrations", "/automations", "/functions", "/settings" })
        {
            (await sheet.Locator($"a[href$='{route}']").CountAsync())
                .ShouldBe(1, $"the sheet is the only way to reach {route} on a phone");
        }

        (await sheet.GetByText("Sign out").CountAsync()).ShouldBe(1);

        await sheet.Locator("a[href$='/history']").ClickAsync();
        await session.Page.WaitForURLAsync("**/admin/history");
        await session.SettleAsync();
        await session.AssertRenderedAsync();

        /* Waiting for it to leave rather than counting it: the sheet closes through the circuit, so
           a count taken the instant the URL changed is a snapshot of a render that has not arrived —
           the same impatience the goto-shortcut scenario records below. */
        await session.Page.Locator("[data-testid='sections-sheet']")
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The phone's bottom bar carries five sections and every one of them works.
    /// </summary>
    /// <remarks>
    /// Design §4.3 makes this an acceptance criterion rather than a layout preference: the bar is
    /// the whole navigation at 375 px, so an entry leading to something that does not work is a
    /// navigation that lies.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_phone_bar_leads_only_to_sections_that_work()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("");

        var items = session.Page.Locator(".a-bottomnav .a-bottomnav__item");
        (await items.CountAsync()).ShouldBe(5);

        for (var index = 0; index < 5; index += 1)
        {
            await items.Nth(index).ClickAsync();
            await session.SettleAsync();
            await session.AssertRenderedAsync();

            (await session.Page.Locator("main.a-content").InnerTextAsync())
                .ShouldNotContain("Not yet");
        }

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A "not yet" section opens and breaks nothing.
    /// </summary>
    /// <remarks>
    /// Design §6.2 asks for exactly this test, and says why: it is trivial, and it is the one that
    /// fires when somebody removes an entry from <c>UnhonouredSubsystems</c> and forgets the UI.
    /// </remarks>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("/automations")]
    [InlineData("/functions")]
    public async Task A_not_yet_section_opens_and_says_what_does_not_happen(string route)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync(route);

        await session.AssertRenderedAsync();
        (await session.Page.Locator("main.a-content").InnerTextAsync()).ShouldContain("Not yet");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// The palette opens from the keyboard, and takes you somewhere.
    /// </summary>
    /// <remarks>
    /// F5's sixth acceptance criterion is that every primary flow completes without a mouse. This
    /// is its spine: ⌘K, type, Enter — and the focus has to be inside the input for the typing to
    /// land anywhere, which is the part that silently regresses.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_command_palette_opens_from_the_keyboard_and_navigates()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.Keyboard.PressAsync("Meta+k");
        await session.Page.Locator(".a-palette").WaitForAsync();

        await session.Page.Keyboard.TypeAsync("access");

        /* Wait for the filter to have produced the match, not for a timer. Enter opens whatever is
           selected, so pressing it before the typed query has round-tripped opens whatever the list
           held a moment ago — which on a slower runner is the unfiltered first entry, and the
           navigation this fact waits for never comes. */
        await session.Page.Locator("[role='option'][aria-selected='true']:has-text('Access')")
            .WaitForAsync();

        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForURLAsync("**/admin/access");
        await session.SettleAsync();
        session.AssertConsoleClean();
    }

    /// <summary>
    /// <c>g</c> then a letter jumps to a section, and Escape dismisses whatever is open.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_goto_shortcut_and_escape_both_work()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.Keyboard.PressAsync("g");
        await session.Page.Keyboard.PressAsync("s");

        /* Waiting for the URL rather than settling and then reading it: the shortcut navigates
           through the circuit, so the answer arrives over the WebSocket — and network-idle, which
           is what settling measures, is true the whole time a WebSocket is quiet. Reading the URL
           straight after a settle therefore reads it before the navigation has happened, and the
           test fails for its own impatience. */
        await session.Page.WaitForURLAsync("**/admin/schema");
        await session.SettleAsync();

        /* Waiting for the element rather than counting it, for the reason above: the palette opens
           over the circuit, so it is not in the DOM the instant the key is released. A count is a
           snapshot; WaitForAsync is the question actually being asked. */
        await session.Page.Keyboard.PressAsync("Meta+k");
        await session.Page.Locator(".a-palette").WaitForAsync();

        await session.Page.Keyboard.PressAsync("Escape");
        await session.Page.Locator(".a-palette")
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

        session.AssertConsoleClean();
    }

    /// <summary>
    /// <c>g d</c> from Overview lands on Data, and <c>g ,</c> — the one key that is not a letter — on Settings.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_goto_shortcut_reaches_data_and_settings()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.Keyboard.PressAsync("g");
        await session.Page.Keyboard.PressAsync("d");
        await session.Page.WaitForURLAsync("**/admin/data");
        await session.SettleAsync();

        await session.Page.Keyboard.PressAsync("g");
        await session.Page.Keyboard.PressAsync(",");
        await session.Page.WaitForURLAsync("**/admin/settings");
        await session.SettleAsync();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A sheet open over the page holds the jump back — a stray <c>g d</c> must not navigate out from under it.
    /// </summary>
    /// <remarks>
    /// A negative, so it waits a fixed moment; the positive twin above shows the same keys do navigate
    /// when nothing is open, which is what keeps this from passing for a shortcut that is simply broken.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_goto_shortcut_is_held_while_a_sheet_is_open()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.ClickAsync("[data-testid='rename-entity']");
        await session.Page.Locator("[data-testid='rename-sheet']").WaitForAsync();
        await session.Page.EvaluateAsync("document.activeElement?.blur()");

        await session.Page.Keyboard.PressAsync("g");
        await session.Page.Keyboard.PressAsync("d");
        await session.Page.WaitForTimeoutAsync(750);

        session.Page.Url.ShouldEndWith("/admin/schema/work_orders");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// The palette's Toggle theme flips the theme, and the header's toggle follows a flip it did not make.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Toggle_theme_from_the_palette_and_the_header_both_flip_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        var toggle = session.Page.Locator("[data-testid='theme-toggle']");
        await session.Page.WaitForFunctionAsync(
            "document.querySelector(\"[data-testid='theme-toggle']\")?.getAttribute('aria-label')?.startsWith('Switch')");
        var before = await Theme(session);

        await session.Page.Keyboard.PressAsync("Meta+k");
        await session.Page.Locator(".a-palette").WaitForAsync();
        await session.Page.WaitForFunctionAsync("document.activeElement?.classList.contains('a-palette__input')");
        await session.Page.Keyboard.TypeAsync("toggle theme");
        await session.Page.Locator("[role='option'][aria-selected='true']:has-text('Toggle theme')").WaitForAsync();
        await session.Page.Keyboard.PressAsync("Enter");

        var flipped = before == "dark" ? "light" : "dark";
        await session.Page.WaitForFunctionAsync($"document.documentElement.dataset.theme === '{flipped}'");
        await session.Page.WaitForFunctionAsync(
            $"document.querySelector(\"[data-testid='theme-toggle']\").getAttribute('aria-label') === 'Switch to {before} theme'");

        await toggle.ClickAsync();
        await session.Page.WaitForFunctionAsync($"document.documentElement.dataset.theme === '{before}'");
        await session.Page.WaitForFunctionAsync(
            $"document.querySelector(\"[data-testid='theme-toggle']\").getAttribute('aria-label') === 'Switch to {flipped} theme'");

        session.AssertConsoleClean();
    }

    /// <summary>The theme the page resolves to — the stored choice, else the system's.</summary>
    private static Task<string> Theme(AdminSession session) => session.Page.EvaluateAsync<string>(
        "document.documentElement.dataset.theme ?? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')");

    /// <summary>
    /// An entity's tab is in its address: a reload opens on it, the arrows move it without a history step,
    /// a click is one, and Back returns.
    /// </summary>
    /// <remarks>
    /// The reload is the half that matters to an operator — a link somebody was sent — and the only
    /// half a component test cannot reach, because it is the address arriving cold at the server.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_tab_survives_a_reload_and_follows_history()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=rules");

        await session.Page.ReloadAsync();
        await session.SettleAsync();
        var selected = session.Page.Locator("[role='tab'][aria-selected='true']");
        await session.Page.Locator("[role='tab'][aria-selected='true']:has-text('Rules')").WaitForAsync();
        (await selected.CountAsync()).ShouldBe(1);

        await selected.FocusAsync();
        await session.Page.Keyboard.PressAsync("ArrowRight");
        await session.Page.WaitForURLAsync("**/admin/schema/work_orders?tab=on-write");
        await session.Page.Locator("[role='tab'][aria-selected='true']:has-text('On write')").WaitForAsync();
        (await session.Page.EvaluateAsync<string>("document.activeElement.textContent")).ShouldBe("On write");

        /* The arrow replaced the entry, so the click is the one step Back undoes — to On write, not Rules. */
        await session.OpenTabAsync("Indexes");
        await session.Page.WaitForURLAsync("**/admin/schema/work_orders?tab=indexes");

        await session.Page.GoBackAsync();
        await session.Page.WaitForURLAsync("**/admin/schema/work_orders?tab=on-write");
        await session.Page.Locator("[role='tab'][aria-selected='true']:has-text('On write')").WaitForAsync();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The palette lists the shortcuts beside the sections, and its New entity opens the form on Schema.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_palette_teaches_the_shortcuts_and_opens_a_new_entity()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.Keyboard.PressAsync("Meta+k");
        await session.Page.Locator(".a-palette [role='option']:has-text('Data') kbd:has-text('g d')").WaitForAsync();
        await session.Page.WaitForFunctionAsync("document.activeElement?.classList.contains('a-palette__input')");

        await session.Page.Keyboard.TypeAsync("new entity");
        await session.Page.Locator("[role='option'][aria-selected='true']:has-text('New entity')").WaitForAsync();
        await session.Page.Keyboard.PressAsync("Enter");

        await session.Page.Locator("[data-testid='new-entity']").WaitForAsync();
        await session.Page.WaitForFunctionAsync("document.activeElement?.id === 'new-entity-name'");

        session.AssertConsoleClean();
    }
}
