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
