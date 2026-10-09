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

        /* Then sent on purpose. An Enter that had sent would have asked "line one" first, as a turn of its own:
           the conversation is exactly one question and its answer, and the question carries both lines. */
        await session.Page.Keyboard.PressAsync("Control+Enter");
        await WaitForTurnToEndAsync(session, turns: 2);
        (await Turns(session).CountAsync()).ShouldBe(2);
        (await Turns(session).First.InnerTextAsync()).ShouldContain("line one\nline two");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_thread_follows_the_newest_turn_unless_the_operator_scrolled_up()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken, height: 560);
        for (var turn = 0; turn < 6; turn++)
        {
            await AskAsync(session, $"question {turn}: add an invoices entity");
        }

        await WaitForThreadAsync(session, "e => e.scrollHeight - e.scrollTop - e.clientHeight < 4");

        await ScrollToTopAsync(session);
        (await Thread(session).GetAttributeAsync("data-alvo-follow")).ShouldBe("off", "the operator's scroll was heard");
        await AskAsync(session, "one more question");
        await WaitForTurnToEndAsync(session, turns: 14);
        await WaitForFollowDecisionAsync(session);
        (await Thread(session).EvaluateAsync<double>("e => e.scrollTop")).ShouldBe(0, "the thread stayed where the operator put it");
    }

    /// <summary>
    /// The page's own scroll to the newest turn is not the operator scrolling up, even when the thread grows before the
    /// browser delivers it. The browser delivers a scroll a frame after <c>scrollTop</c> is set, and a streamed turn
    /// redraws the thread in between; measured then, the gap read as an operator's scroll and following stopped with
    /// nobody having scrolled — the race that made the scenario above fail one CI run in three.
    /// </summary>
    /// <remarks>
    /// Driven through the module's own <c>followNewest</c> rather than by asking questions, because the redraw has to
    /// land in that one frame on every run, and a turn streamed from the circuit lands there only sometimes.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_turn_drawn_before_the_follow_scroll_is_delivered_does_not_stop_the_following()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken, height: 560);
        await AskAsync(session, "add an invoices entity");
        await WaitForTurnToEndAsync(session, turns: 2);
        await WaitForFollowDecisionAsync(session);

        var follow = await Thread(session).EvaluateAsync<string>(
            """
            async (thread, module) => {
              const { followNewest } = await import(module);
              const scrolled = () => Promise.race([
                new Promise(done => thread.addEventListener('scroll', done, { once: true })),
                new Promise(done => setTimeout(done, 2000))]);
              const turn = () => {
                const probe = document.createElement('div');
                probe.style.height = '200px';
                probe.style.flex = 'none';
                thread.append(probe);
              };

              /* At the bottom, following, as an operator who has not scrolled leaves it. */
              turn();
              let delivered = scrolled();
              thread.scrollTop = thread.scrollHeight;
              await delivered;
              if (thread.dataset.alvoFollow !== 'on') return `not following before the probe: ${thread.dataset.alvoFollow}`;

              /* A turn lands; the follow scrolls to it; the next turn is drawn before that scroll is delivered. */
              turn();
              delivered = scrolled();
              followNewest(thread);
              requestAnimationFrame(turn);
              await delivered;
              return thread.dataset.alvoFollow;
            }
            """,
            AlvoAdminAssets.Module);

        follow.ShouldBe("on", "nobody scrolled: the thread grew under the page's own scroll");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_pane_is_a_labelled_non_modal_region_as_tall_as_the_window_and_announces_turns()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken, height: 950);
        var pane = Pane(session);

        (await session.Page.Locator("[aria-modal='true']").CountAsync()).ShouldBe(0, "the pane is beside the page, not over it");
        (await session.Page.GetByTestId("assistant-thread").GetAttributeAsync("aria-live")).ShouldBe("polite");
        (await session.Page.GetByTestId("assistant-send").IsDisabledAsync()).ShouldBeTrue("nothing to send yet");

        var box = await AdminSession.SettledBoxAsync(pane);
        var header = await AdminSession.SettledBoxAsync(session.Page.GetByTestId("appbar"));
        var content = await AdminSession.SettledBoxAsync(session.Content);
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
        var box = await AdminSession.SettledBoxAsync(Pane(session));

        box.X.ShouldBe(0, 1);
        box.Width.ShouldBe(375, 1);
        await session.AssertNoHorizontalScrollAsync();
        (await PageIsInertAsync(session)).ShouldBeTrue("the page under the pane is out of the tab order");

        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Close the assistant" }).ClickAsync();
        await session.Page.WaitForFunctionAsync("() => !document.getElementById('a-content')?.closest('[inert]')");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Above the phone and up to 920 px the pane still covers the page: beside it the page column would be narrower than
    /// the 480 px a table needs (360 px at 800), while the bottom bar and the nav drawer stay a desktop's.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Up_to_920_px_the_pane_covers_the_page_which_is_inert_until_it_closes()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 800);
        await session.GoAsync("/schema");
        (await session.Page.GetByTestId("bottom-nav").IsVisibleAsync()).ShouldBeFalse("800 px is not a phone");

        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        await session.Page.WaitForFunctionAsync("() => !!document.getElementById('a-content')?.closest('[inert]')");
        var box = await AdminSession.SettledBoxAsync(Pane(session));

        box.X.ShouldBe(0, 1);
        box.Width.ShouldBe(800, 1);
        (await AdminSession.SettledBoxAsync(session.Content)).Width.ShouldBeGreaterThanOrEqualTo(780, "the page keeps its own width under the pane");
        await session.AssertNoHorizontalScrollAsync();

        /* The app bar stays above the pane, and its launcher is the way out as much as the pane's own close. */
        var header = await AdminSession.SettledBoxAsync(session.Page.GetByTestId("appbar"));
        header.Y.ShouldBe(0, 1);
        box.Y.ShouldBeGreaterThanOrEqualTo(header.Y + header.Height - 1, "the pane does not cover the app bar");
        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        await session.Page.WaitForFunctionAsync("() => !document.getElementById('a-content')?.closest('[inert]')");

        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Close the assistant" }).ClickAsync();
        await session.Page.WaitForFunctionAsync("() => !document.getElementById('a-content')?.closest('[inert]')");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Between_920_and_1100_px_the_nav_drawer_steps_aside_so_the_page_keeps_its_width()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 1024);
        await session.GoAsync("/schema");
        var sidebar = session.Page.GetByTestId("sidebar");
        await sidebar.WaitForAsync();

        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        await sidebar.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        (await AdminSession.SettledBoxAsync(session.Content)).Width.ShouldBeGreaterThanOrEqualTo(480, "the page beside the pane is still usable");
        (await PageIsInertAsync(session)).ShouldBeFalse("beside the pane, not under it");

        await session.Page.Keyboard.PressAsync("Escape");
        await sidebar.WaitForAsync();
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
        await session.WaitForFocusOnAsync("assistant-message");
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

        await WaitForTurnToEndAsync(session, turns: 2);
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

        var contrast = (await ContrastProbe.ReadAsync(send)).ShouldHaveSingleItem();
        contrast.Ratio.ShouldBeGreaterThanOrEqualTo(ContrastProbe.AA, contrast.ToString());
    }

    /// <summary>
    /// A render of the shell for anything but the thread asks the browser nothing about the thread (final review M16):
    /// the pane redrew and ran the follow on every layout render, streamed or not, open or closed.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Moving_between_screens_asks_the_thread_nothing()
    {
        await using var session = await OpenAsync(TestContext.Current.CancellationToken);
        await AskAsync(session, "add an invoices entity");
        await WaitForTurnToEndAsync(session, turns: 2);
        await session.Page.WaitForTimeoutAsync(300);
        await session.Page.GetByTestId("assistant-thread").EvaluateAsync(
            """
            thread => {
              window.__alvoFollows = 0;
              new MutationObserver(records => { window.__alvoFollows += records.length; })
                .observe(thread, { attributes: true, attributeFilter: ['data-alvo-followed-at'] });
            }
            """);

        foreach (var section in new[] { "Data", "Access", "Schema" })
        {
            await session.Page.GetByTestId("sidebar").GetByRole(AriaRole.Link, new() { Name = section, Exact = true }).ClickAsync();
            await session.Page.GetByRole(AriaRole.Heading, new() { Level = 1, Name = section }).WaitForAsync();
        }

        await session.Page.WaitForTimeoutAsync(300);
        (await session.Page.EvaluateAsync<int>("() => window.__alvoFollows")).ShouldBe(0, "no layout render follows the thread");
    }

    private async Task<AdminSession> OpenAsync(CancellationToken cancel, int height = 900)
    {
        var session = await world.SignInAsync(cancel);
        await session.Page.SetViewportSizeAsync(1400, height);
        await session.GoAsync("/schema");
        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        await session.Page.GetByTestId("assistant-drawer").WaitForAsync();
        await session.WaitForFocusOnAsync("assistant-message");
        return session;
    }

    /// <summary>The pane, found as a screen reader finds it: the complementary region named for the assistant.</summary>
    private static ILocator Pane(AdminSession session)
        => session.Page.GetByRole(AriaRole.Complementary, new() { Name = "Ask Alvo", Exact = true });

    private static Task<bool> PageIsInertAsync(AdminSession session)
        => session.Page.EvaluateAsync<bool>("() => !!document.getElementById('a-content')?.closest('[inert]')");

    private static ILocator Thread(AdminSession session) => session.Page.GetByTestId("assistant-thread");

    /// <summary>
    /// Waits until the thread holds <paramref name="turns"/> turns and the last has been filed: Ask is no longer
    /// busy, which End() draws last. The count comes first, because before the turn starts Ask is not busy either.
    /// </summary>
    private static async Task WaitForTurnToEndAsync(AdminSession session, int turns)
        => await session.Page.WaitForFunctionAsync(
            """
            n => document.querySelectorAll("[data-testid='assistant-turn']").length >= n
              && !document.querySelector("[data-testid='assistant-send']")?.hasAttribute('aria-busy')
            """, turns);

    /// <summary>
    /// Waits until followNewest has decided on the thread as it is now: the height it last decided on is the
    /// thread's current one. Without this, a thread that did not follow is indistinguishable from one the
    /// after-render call has not reached yet.
    /// </summary>
    private static Task WaitForFollowDecisionAsync(AdminSession session)
        => WaitForThreadAsync(session, "e => e.dataset.alvoFollowedAt === String(e.scrollHeight)");

    /// <summary>
    /// Waits, with the page's timeout, until <paramref name="predicate"/> holds for the thread, and says where the thread
    /// stood when it did not: "timed out" alone cannot tell a follow that never ran from one that decided not to.
    /// </summary>
    private static async Task WaitForThreadAsync(AdminSession session, string predicate)
    {
        var thread = await Thread(session).ElementHandleAsync();
        try
        {
            await session.Page.WaitForFunctionAsync($"e => ({predicate})(e)", thread);
        }
        catch (TimeoutException timeout)
        {
            var state = await thread.EvaluateAsync<string>(
                "e => `scrollTop ${e.scrollTop}, scrollHeight ${e.scrollHeight}, clientHeight ${e.clientHeight}, "
                + "follow ${e.dataset.alvoFollow}, decided at ${e.dataset.alvoFollowedAt}`");
            throw new TimeoutException($"The thread never satisfied {predicate}: {state}.", timeout);
        }
    }

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

}
