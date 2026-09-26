using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The assistant is typed to the way every chat box is: Ctrl/Cmd+Enter sends, Enter is a newline, the box empties,
/// and the thread follows the newest turn (inventory defects #1–#3; spec §4 "Assistant"). It is a pane beside the
/// page, opened from the app bar, as tall as the window below it.
/// </summary>
/// <remarks>Its own world: a working copy is per operator, and the scripted assistant answers every question.</remarks>
/// <param name="world">A host with the scripted assistant.</param>
public sealed class AssistantKeyboardScenarios(AssistantWorld world) : IClassFixture<AssistantWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("Control+Enter")]
    [InlineData("Meta+Enter")]
    public async Task The_chord_sends_the_question_and_empties_the_box(string chord)
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken);

        await session.Page.Locator("#assistant-message").FillAsync("add an invoices entity");
        await session.Page.Locator("#assistant-message").PressAsync(chord);

        await Turns(session).First.WaitForAsync();
        (await Turns(session).First.InnerTextAsync()).ShouldContain("add an invoices entity");
        await session.Page.WaitForFunctionAsync("() => document.getElementById('assistant-message')?.value === ''");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Enter_alone_is_a_newline_and_sends_nothing()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken);
        await session.Page.Locator("#assistant-message").FocusAsync();

        await session.Page.Keyboard.TypeAsync("line one");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.Keyboard.TypeAsync("line two");

        (await session.Page.InputValueAsync("#assistant-message")).ShouldBe("line one\nline two");
        (await Turns(session).CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_thread_follows_the_newest_turn_unless_the_operator_scrolled_up()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken, height: 560);
        for (var turn = 0; turn < 6; turn++)
        {
            await AskAsync(session, $"question {turn}: add an invoices entity");
        }

        (await AtBottomAsync(session)).ShouldBeTrue("the newest turn is in view");

        await ScrollToTopAsync(session);
        await AskAsync(session, "one more question");
        (await session.Page.GetByTestId("assistant-thread").EvaluateAsync<double>("e => e.scrollTop")).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_pane_is_a_labelled_non_modal_region_as_tall_as_the_window_and_announces_turns()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken, height: 950);
        var pane = Pane(session);

        (await session.Page.Locator("[aria-modal='true']").CountAsync()).ShouldBe(0, "the pane is beside the page, not over it");
        (await session.Page.GetByTestId("assistant-thread").GetAttributeAsync("aria-live")).ShouldBe("polite");
        (await session.Page.GetByTestId("assistant-send").IsDisabledAsync()).ShouldBeTrue("nothing to send yet");

        var box = await BoxAsync(pane);
        var header = await BoxAsync(session.Page.GetByTestId("appbar"));
        var content = await BoxAsync(session.Content);
        (box.Y + box.Height).ShouldBe(950, 1, "the pane reaches the bottom of the window");
        box.Y.ShouldBeGreaterThanOrEqualTo(header.Y + header.Height - 1, "the pane does not cover the header");
        (content.X + content.Width).ShouldBeLessThanOrEqualTo(box.X + 1, "the page beside the pane is not covered");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_pane_is_the_full_width_of_a_phone_and_its_launcher_is_an_icon()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/schema");
        var launcher = session.Page.GetByRole(AriaRole.Button, new() { Name = "Ask Alvo", Exact = true });

        (await launcher.InnerTextAsync()).Trim().ShouldBeEmpty("an icon on a phone, named by its label");
        await launcher.ClickAsync();
        var box = await BoxAsync(Pane(session));

        box.X.ShouldBe(0, 1);
        box.Width.ShouldBe(375, 1);
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Opening_focuses_the_question_and_closing_returns_focus_to_the_launcher()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken);
        var launcher = session.Page.GetByTestId("assistant-launch");

        (await session.FocusedAsync()).ShouldStartWith("textarea#assistant-message");
        (await launcher.GetAttributeAsync("aria-expanded")).ShouldBe("true");

        await session.Page.Keyboard.PressAsync("Escape");
        await Pane(session).WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        (await session.FocusedAsync()).ShouldEndWith("[assistant-launch]");
        (await launcher.GetAttributeAsync("aria-expanded")).ShouldBe("false");

        await launcher.ClickAsync();
        await WaitForFocusAsync(session, "assistant-message");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Close the assistant" }).ClickAsync();
        await Pane(session).WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        (await session.FocusedAsync()).ShouldEndWith("[assistant-launch]");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Send_is_busy_while_a_turn_streams_and_the_box_stays_open_for_the_next_question()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken);
        var send = session.Page.GetByTestId("assistant-send");

        await session.Page.Locator("#assistant-message").FillAsync("slowly add an invoices entity");
        await send.ClickAsync();

        await session.Page.WaitForFunctionAsync(
            "() => document.querySelector(\"[data-testid='assistant-send']\")?.getAttribute('aria-busy') === 'true'");
        (await send.IsDisabledAsync()).ShouldBeTrue("a second press would ask twice");
        (await session.Page.Locator("#assistant-message").IsEditableAsync()).ShouldBeTrue("the next question can be typed");

        await session.Page.GetByTestId("assistant-proposal").WaitForAsync();
        (await send.GetAttributeAsync("aria-busy")).ShouldBeNull();
        (await Turns(session).CountAsync()).ShouldBe(2);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_failed_turn_is_an_alert_in_the_pane_that_takes_focus_and_never_a_snackbar()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken);

        await session.Page.Locator("#assistant-message").FillAsync("fail to add an invoices entity");
        await session.Page.Locator("#assistant-message").PressAsync("Control+Enter");

        var alert = Pane(session).GetByTestId("error-panel");
        await alert.WaitForAsync();
        (await alert.InnerTextAsync()).ShouldContain(ScriptedAssistant.FailureText);
        await session.Page.WaitForFunctionAsync(
            "() => !!document.activeElement?.closest(\"[data-testid='error-panel']\")");
        (await session.SnackbarCountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task The_ask_button_reads_at_AA_contrast_in_both_themes(ColorScheme scheme)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, colorScheme: scheme);
        await session.GoAsync("/schema");
        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        await session.Page.Locator("#assistant-message").FillAsync("add an invoices entity");
        var send = session.Page.GetByTestId("assistant-send");
        await session.Page.WaitForFunctionAsync(
            "() => !document.querySelector(\"[data-testid='assistant-send']\")?.disabled");

        var contrast = await ContrastAsync(send);
        contrast.Ratio.ShouldBeGreaterThanOrEqualTo(4.5, $"{contrast.Color} on {contrast.Background}");
    }

    private async Task<AdminSession> OpenAsync(CancellationToken cancel, int height = 900)
    {
        var session = await world.SignInAsync(cancel);
        await session.Page.SetViewportSizeAsync(1400, height);
        await session.GoAsync("/schema");
        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        await session.Page.GetByTestId("assistant-drawer").WaitForAsync();
        await WaitForFocusAsync(session, "assistant-message");
        return session;
    }

    /// <summary>The pane, found as a screen reader finds it: the complementary region named for the assistant.</summary>
    private static ILocator Pane(AdminSession session)
        => session.Page.GetByRole(AriaRole.Complementary, new() { Name = "Ask Alvo", Exact = true });

    private static ILocator Turns(AdminSession session)
        => session.Page.GetByTestId("assistant-thread").GetByTestId("assistant-turn");

    private static async Task AskAsync(AdminSession session, string question)
    {
        var before = await Turns(session).CountAsync();
        await session.Page.Locator("#assistant-message").FillAsync(question);
        await session.Page.Locator("#assistant-message").PressAsync("Control+Enter");
        await session.Page.WaitForFunctionAsync(
            "n => document.querySelectorAll(\"[data-testid='assistant-turn']\").length >= n + 2", before);
    }

    /// <summary>
    /// Scrolls the thread to its top as the operator would, and waits until the browser has delivered the scroll:
    /// it does so on the next frame, and an operator's scroll is never a single frame long.
    /// </summary>
    private static async Task ScrollToTopAsync(AdminSession session)
        => await session.Page.GetByTestId("assistant-thread").EvaluateAsync(
            "e => new Promise(done => { e.addEventListener('scroll', () => done(), { once: true }); e.scrollTop = 0; })");

    private static Task<bool> AtBottomAsync(AdminSession session)
        => session.Page.GetByTestId("assistant-thread")
            .EvaluateAsync<bool>("e => e.scrollHeight - e.scrollTop - e.clientHeight < 4");

    /// <summary>Waits until focus is on the element with <paramref name="id"/>; the pane slides in first.</summary>
    private static async Task WaitForFocusAsync(AdminSession session, string id)
        => await session.Page.WaitForFunctionAsync("id => document.activeElement?.id === id", id);

    /// <summary>The element's box once nothing on the page is moving.</summary>
    /// <remarks>
    /// The pane slides in, and the page beside it narrows with it; a box read mid-slide is the animation's, not the
    /// layout's. Every running animation is waited out rather than a guessed number of milliseconds.
    /// </remarks>
    private static async Task<LocatorBoundingBoxResult> BoxAsync(ILocator element)
    {
        await element.Page.WaitForFunctionAsync(
            "() => document.getAnimations().every(a => a.playState !== 'running')");
        var box = await element.BoundingBoxAsync();
        box.ShouldNotBeNull();
        return box;
    }

    /// <summary>
    /// The WCAG contrast ratio of an element's text against its own background, as the browser resolved both.
    /// </summary>
    private static async Task<Contrast> ContrastAsync(ILocator element)
    {
        /* A button eases from its disabled colours to its own; a pair read mid-transition is neither. */
        await element.Page.WaitForFunctionAsync(
            "() => document.getAnimations().every(a => a.playState !== 'running')");
        return await element.EvaluateAsync<Contrast>(
        """
        e => {
          /* Painted rather than parsed: a computed colour may be rgb(), oklch() or color(srgb …). */
          const context = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
          const rgb = v => { context.clearRect(0, 0, 1, 1); context.fillStyle = v; context.fillRect(0, 0, 1, 1);
            return [...context.getImageData(0, 0, 1, 1).data].slice(0, 3); };
          const lum = ([r, g, b]) => [r, g, b].map(c => { c /= 255; return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; })
            .reduce((sum, c, i) => sum + c * [0.2126, 0.7152, 0.0722][i], 0);
          const style = getComputedStyle(e);
          const [a, b] = [lum(rgb(style.color)), lum(rgb(style.backgroundColor))];
          return { Ratio: (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05), Color: style.color, Background: style.backgroundColor };
        }
        """);
    }

    /// <summary>A contrast ratio, with the two colours it was measured between.</summary>
    /// <remarks>Settable properties, because Playwright builds a returned object with a parameterless constructor.</remarks>
    private sealed class Contrast
    {
        public double Ratio { get; set; }

        public string Color { get; set; } = string.Empty;

        public string Background { get; set; } = string.Empty;
    }
}
