using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// One signed-in browser session, with the assertions every scenario shares.
/// </summary>
/// <remarks>
/// The three checks the brief for this dashboard asks of every screen live here so no scenario has
/// to remember them: the console is clean, the page does not scroll sideways, and nothing that was
/// rendered is clipped out of view.
/// </remarks>
/// <param name="context">The browser context, disposed with the session.</param>
/// <param name="page">The page.</param>
/// <param name="baseAddress">Where the dashboard is.</param>
public sealed class AdminSession(IBrowserContext context, IPage page, string baseAddress)
    : IAsyncDisposable
{
    /// <summary>
    /// Poll on a timer, never on an animation frame.
    /// </summary>
    /// <remarks>
    /// <b>Playwright's default polling for <c>WaitForFunction</c> is <c>raf</c>, and Chromium stops
    /// firing animation frames for a page that is not visible.</b> One browser context is always
    /// visible, so a single scenario passes; the moment a class opens a second one, the first
    /// page's poller stops and the wait hangs until it times out. That is the whole explanation for
    /// a suite where every test passes alone and the class does not finish.
    /// </remarks>
    private static readonly PageWaitForFunctionOptions _polling = new() { PollingInterval = 100 };

    private readonly List<string> _noise = [];

    /// <summary>The page every scenario drives.</summary>
    public IPage Page { get; } = page;

    /// <summary>Everything the page said that it should not have.</summary>
    public IReadOnlyList<string> Noise => _noise;

    /// <summary>Starts watching the console. Called by the world before it navigates anywhere.</summary>
    /// <remarks>
    /// Before the first navigation rather than after it, because the errors worth catching are the
    /// ones raised <em>while the page loads</em> — a framework script that 404s, a module that
    /// throws on import. A listener attached afterwards sees a quiet console and reports a clean
    /// page that never became interactive.
    /// </remarks>
    public void Watch()
    {
        Page.Console += (_, message) =>
        {
            if (message.Type is "error")
            {
                _noise.Add($"console.error: {message.Text}");
            }
        };

        Page.RequestFailed += (_, request) =>
        {
            /* `_blazor/disconnect` is the beacon the circuit fires as the page unloads, so the
               server can release it immediately instead of waiting for a timeout. The browser
               cancels it mid-flight by design — the page is going away — and it is therefore the
               one failed request that means the framework is working. Every other one is real. */
            if (!request.Url.EndsWith("/_blazor/disconnect", StringComparison.Ordinal))
            {
                _noise.Add($"requestfailed: {request.Url}");
            }
        };
        Page.PageError += (_, error) => _noise.Add($"pageerror: {error}");
    }

    /// <summary>Opens a dashboard route.</summary>
    /// <param name="path">A path under the dashboard, such as <c>/schema</c>.</param>
    /// <returns>A task that completes once the screen has settled.</returns>
    public async Task GoAsync(string path)
    {
        await Page.GotoAsync($"{baseAddress}{AlvoAdmin.BasePath}{path}").ConfigureAwait(false);
        await SettleAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Follows the shell's pending bar to Preview, the way an operator reaches it after staging an edit.
    /// </summary>
    /// <remarks>
    /// Staging a field used to jump to Preview by itself, and a scenario waited for the URL. It stays on the
    /// entity now (design pass §4.1), so a scenario that wants the diff takes the bar there — and waits for the
    /// plan control rather than the URL alone, because the URL moves before the screen behind it does.
    /// </remarks>
    public async Task PreviewPendingAsync()
    {
        await Page.ClickAsync("[data-testid='pending-preview']").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/changes").ConfigureAwait(false);
        await Page.Locator("button:has-text('Plan this change')").WaitForAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Opens one of an entity's tabs and waits for it to actually be the open one.
    /// </summary>
    /// <remarks>
    /// The click re-renders over the circuit, so reading the panel straight afterwards reads the
    /// tab that was open before. Waiting on the active class is waiting on the thing the click was
    /// for.
    /// </remarks>
    /// <param name="tab">The tab's label.</param>
    public async Task OpenTabAsync(string tab)
    {
        await Page.ClickAsync($"button.a-tab:has-text('{tab}')").ConfigureAwait(false);
        await Page.Locator($"button.a-tab--active:has-text('{tab}')").WaitForAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the screen to stop moving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not network-idle.</b> A server-interactive page holds a SignalR connection open for its
    /// whole life and sends a keep-alive down it every few seconds, so "no network activity for
    /// 500 ms" is a condition that may simply never hold — and a helper called before every
    /// assertion then turns a fifteen-second scenario into one that never finishes. The signals
    /// that mean something here are the circuit being up, the keyboard being wired, and the
    /// animations having finished; everything else is waited for by the locator that needs it.
    /// </para>
    /// </remarks>
    public async Task SettleAsync()
    {

        /* And wait for the circuit, which network-idle does NOT imply: a server-interactive
           page renders its first HTML from an ordinary response, so the network goes quiet
           while the components are still inert. A click landing in that window does nothing at
           all — no error, no handler — which is exactly the kind of failure that looks like a
           broken control and is really a race. */
        try
        {
            await Page.WaitForFunctionAsync("() => !!window.Blazor", null, _polling)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"The page at {Page.Url} never became interactive — window.Blazor is absent. "
                + $"The console said: {(_noise.Count == 0 ? "nothing" : string.Join(" | ", _noise))}");
        }

        /* And wait for the keyboard, which the circuit does NOT imply either. window.Blazor appears
           when the connection opens; the palette subscribes to the key map afterwards, from its own
           OnAfterRenderAsync. A keystroke sent in between is swallowed silently — the map runs and
           nothing is listening — which is a race that looks exactly like a broken shortcut. */
        try
        {
            await Page.WaitForFunctionAsync(
                "() => document.documentElement.dataset.alvoKeyboard === 'ready'", null, _polling)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"The keyboard at {Page.Url} never became live. "
                + $"The console said: {(_noise.Count == 0 ? "nothing" : string.Join(" | ", _noise))}");
        }
        await Page.EvaluateAsync(
            "() => Promise.all(document.getAnimations().map(a => a.finished.catch(() => {})))")
            .ConfigureAwait(false);
    }

    /// <summary>Fails when the page logged anything it should not have.</summary>
    public void AssertConsoleClean()
        => _noise.ShouldBeEmpty($"the page logged {_noise.Count} thing(s) it should not have");

    /// <summary>
    /// Fails when the document scrolls sideways.
    /// </summary>
    /// <remarks>
    /// F5's first acceptance criterion, measured rather than eyeballed. A table, a diagram or a code
    /// block may scroll inside its own container; the document may not.
    /// </remarks>
    /// <summary>
    /// Asserts that no text on the page is wrapping one character per line.
    /// </summary>
    /// <remarks>
    /// <b>The failure a horizontal-scroll check cannot see.</b> A flex container turns every inline
    /// child into a flex item; one that also carries <c>overflow-wrap: anywhere</c> then shrinks to a
    /// single character wide and runs down the page as a column of letters. The page does not
    /// overflow, every element is visible, and the screen is unreadable — which is how a sentence
    /// about <c>IApiKeyStore</c> reached a phone as five vertical strips.
    /// </remarks>
    public async Task AssertNoVerticalTextAsync()
    {
        await SettleAsync().ConfigureAwait(false);

        /* A leaf with real text, narrower than about two characters and taller than a couple of
           lines, is not a narrow column — it is a word broken per character. */
        var offenders = await Page.EvaluateAsync<string[]>(
            "() => { const bad = []; for (const el of document.querySelectorAll('body *')) {"
            + " if (el.children.length) continue;"
            + " const text = (el.textContent || '').trim(); if (text.length < 6) continue;"
            + " const style = getComputedStyle(el);"
            + " if (style.display === 'none' || style.visibility === 'hidden') continue;"
            + " const box = el.getBoundingClientRect();"
            + " const size = parseFloat(style.fontSize) || 14;"
            + " if (box.width > 0 && box.width < size * 2.2 && box.height > size * 2.5) {"
            + "   bad.push(el.tagName.toLowerCase() + '.' + (el.className || '(no class)')"
            + "     + ' \"' + text.slice(0, 24) + '\"'); } }"
            + " return [...new Set(bad)]; }").ConfigureAwait(false);

        offenders.ShouldBeEmpty(
            $"these wrap one character per line at this width: {string.Join(" · ", offenders)}");
    }

    public async Task AssertNoHorizontalScrollAsync()
    {
        await SettleAsync().ConfigureAwait(false);
        /* The document AND the shell's own content pane — those two, not every scroller on the page.
           Measuring only the document was a blind spot the shell guarantees: `.a-content` carries
           `overflow: auto`, so content wider than a phone scrolls that pane while the document never
           moves, which is exactly what an operator sees and what this assertion called clean. It cost a
           real defect — the On write tab shipped with a code block that took the content pane sideways
           at 375 px, past a green `Every_screen_fits_a_phone` that walks that very route.

           Deliberate scrollers are NOT included, and that is the distinction rather than an oversight:
           the design system opts a tab strip, a wide table and a code block into `overflow-x: auto` on
           purpose, and a check that failed on those would be a check somebody turns off. A pane that
           scrolls because it was asked to is a pattern; the page's own frame scrolling is the bug. */
        var overflow = await Page.EvaluateAsync<int>(
            "() => { const doc = document.documentElement;"
            + " const pane = document.querySelector('main.a-content');"
            + " return Math.max(doc.scrollWidth - doc.clientWidth,"
            + "   pane ? pane.scrollWidth - pane.clientWidth : 0); }")
            .ConfigureAwait(false);

        if (overflow <= 1)
        {
            return;
        }

        /* Naming the widest element, because "the page scrolls sideways" sends a reader to
           look at every panel on it. The offender is almost always one element carrying a
           fixed width, or a min-width the viewport cannot satisfy. */
        /* Naming the widest element inside the pane, because "the page scrolls sideways" sends a reader
           to look at every panel on it. The offender is almost always one element carrying a fixed width,
           or a flex item that kept its `min-width: auto` and refused to shrink under its own content. */
        var culprit = await Page.EvaluateAsync<string>(
            "() => { const pane = document.querySelector('main.a-content') || document.body;"
            + " const edge = pane.getBoundingClientRect().right;"
            + " let worst = '', width = edge;"
            + " for (const el of pane.querySelectorAll('*')) {"
            + "   if (/(auto|scroll)/.test(getComputedStyle(el).overflowX)) continue;"
            + "   const right = el.getBoundingClientRect().right;"
            + "   if (right > width) { width = right;"
            + "     worst = el.tagName.toLowerCase() + '.' + (el.className || '(no class)'); } }"
            + " return worst === '' ? 'no single element reaches past the pane'"
            + "   : worst + ' reaches ' + Math.round(width - edge) + 'px past it'; }").ConfigureAwait(false);

        overflow.ShouldBeLessThanOrEqualTo(
            1, $"{Page.Url} scrolls sideways by {overflow}px — {culprit}");
    }

    /// <summary>Fails when the visible shell has nothing in it.</summary>
    /// <remarks>
    /// The cheap guard against the failure this suite exists for: a caught exception that renders
    /// the chrome and no content looks fine in a screenshot and is useless to an operator.
    /// </remarks>
    public async Task AssertRenderedAsync()
    {
        var text = await Page.Locator("main.a-content").InnerTextAsync().ConfigureAwait(false);
        text.Trim().ShouldNotBeEmpty("the content area rendered nothing");
    }

    /// <summary>
    /// Ends the session, letting the circuit go first.
    /// </summary>
    /// <remarks>
    /// <b>Navigating away before closing is what releases the server's circuit.</b> An
    /// interactive page holds a WebSocket open; unloading it fires the <c>_blazor/disconnect</c>
    /// beacon and the host drops the circuit immediately. Closing the context on top of a live
    /// connection instead leaves the host holding it until a timeout, and a class whose next
    /// scenario is waiting for a new one then looks exactly like a hang.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Page.GotoAsync("about:blank").ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            /* The page is already gone; there is nothing left to disconnect. */
        }

        await context.CloseAsync().ConfigureAwait(false);
        await context.DisposeAsync().ConfigureAwait(false);
    }

}
