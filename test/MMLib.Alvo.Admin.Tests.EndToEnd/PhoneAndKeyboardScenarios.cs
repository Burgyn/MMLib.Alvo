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
            await session.AssertRenderedAsync();
        }

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
        await session.SettleAsync();
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
}
