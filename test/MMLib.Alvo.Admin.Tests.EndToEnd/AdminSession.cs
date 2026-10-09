using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// One signed-in browser session, with the assertions every scenario shares.
/// </summary>
/// <remarks>
/// <para>
/// The three checks the brief for this dashboard asks of every screen live here so no scenario has
/// to remember them: the console is clean, the page does not scroll sideways, and nothing that was
/// rendered is clipped out of view.
/// </para>
/// <para>
/// <b>The selector policy</b> (docs/architecture/admin-dashboard-review.md, F-23): a scenario finds an element
/// by its <c>data-testid</c>, or by its role and accessible name — <see cref="Content"/>, <see cref="Button"/>.
/// A design-system class (<c>.a-*</c>) is styling and a <c>:has-text(…)</c> is copy, and a scenario pinned to
/// either breaks on a rename or a reworded sentence that changed nothing it tests.
/// <c>EndToEndSelectorTests</c> holds the count of both down; migrate a scenario when you touch it.
/// </para>
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

    /// <summary>The screen's content: the shell's one <c>main</c> landmark.</summary>
    public ILocator Content => Page.GetByRole(AriaRole.Main);

    /// <summary>A button by its accessible name, which by default it only has to contain — the match <c>:has-text</c> made.</summary>
    /// <param name="name">The name, or a part of it.</param>
    /// <param name="exact">Whether the name must be the whole of it, for a label another button's contains.</param>
    /// <returns>The button; a locator, so an action on it fails if the name is not one button's.</returns>
    public ILocator Button(string name, bool exact = false)
        => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = exact });

    /// <summary>A dialog by its test id: an <c>AlvoEditor</c>, an <c>AlvoConfirm</c> or the palette.</summary>
    /// <remarks>
    /// The <c>role=dialog</c> element that holds the test id, not the element that carries it: the library puts a
    /// dialog's attributes on its content, a level inside the element that is the dialog, so the id alone finds an
    /// element with no role, no <c>aria-modal</c> and none of the dialog's focus trap around it.
    /// </remarks>
    /// <param name="testId">The dialog's test id.</param>
    /// <returns>The dialog.</returns>
    public ILocator Dialog(string testId)
        => Page.GetByRole(AriaRole.Dialog).Filter(new() { Has = Page.GetByTestId(testId) });

    /// <summary>
    /// Loads the page as the server rendered it and holds it there: no circuit starts, so what is on screen is the
    /// first paint, for as long as the scenario needs to measure it.
    /// </summary>
    /// <remarks>
    /// <b>Why not read at DOMContentLoaded.</b> The circuit starts straight after it and draws the interactive tree
    /// over the prerendered one; a reading taken then lands on whichever of the two is attached at that instant, and
    /// was measured returning an empty computed style. The framework's script is answered with an empty one instead
    /// of being refused, so the page logs no failed request.
    /// </remarks>
    /// <param name="page">The page.</param>
    /// <param name="load">The navigation that loads it, such as a reload.</param>
    public static async Task WithoutTheCircuitAsync(IPage page, Func<Task> load)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(load);
        const string framework = "**/_framework/blazor.web.js";
        await page.RouteAsync(framework, route => route.FulfillAsync(new() { ContentType = "text/javascript", Body = string.Empty }))
            .ConfigureAwait(false);
        await load().ConfigureAwait(false);
        await page.UnrouteAsync(framework).ConfigureAwait(false);
    }

    /// <summary>Picks an option of a <c>MudSelect</c> by its visible name.</summary>
    /// <remarks>
    /// Page-scoped on purpose: the library renders options in its popover provider under <c>body</c>, so an option is
    /// never a descendant of the select, nor of the dialog the select sits in (study §5.1 gotcha 1).
    /// <para>
    /// It returns once the select shows the choice. The library hands the choice to the page a JS round trip after the
    /// click, so a box filled straight after it was filled first — and then emptied by the choice that "came later"
    /// (measured: a row's field reset what was typed for it). The select shows the value only once it has passed it on.
    /// </para>
    /// <para>
    /// The wait assumes the select shows exactly the option's name. A select whose shown text differs from its option
    /// label (a <c>ToStringFunc</c>, or a label such as <c>phone (not offered)</c> for a value <c>phone</c>) would never
    /// match and time out here; such a caller waits on something of its own instead.
    /// </para>
    /// </remarks>
    /// <param name="combobox">The select, found by role and name.</param>
    /// <param name="option">The option's name, exactly.</param>
    public async Task ChooseAsync(ILocator combobox, string option)
    {
        ArgumentNullException.ThrowIfNull(combobox);
        await combobox.ClickAsync().ConfigureAwait(false);
        await Page.GetByRole(AriaRole.Option, new() { Name = option, Exact = true }).ClickAsync().ConfigureAwait(false);
        await combobox.Filter(new() { HasTextRegex = new Regex($"^{Regex.Escape(option)}$") }).WaitForAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Chooses the option a select already shows, and waits until its list has gone and focus is back inside
    /// <paramref name="testId"/> — never left on <c>&lt;body&gt;</c>, outside the sheet.
    /// </summary>
    /// <remarks>
    /// A re-choice of the same value is its own case: the library closes the list without raising a value change, so
    /// nothing a choice runs runs. This pins that focus stays inside the select all the same. A loss to the page was
    /// suspected here, and did not reproduce in headless Chromium (MudBlazor 9.10); the pin is what would catch one.
    /// </remarks>
    /// <param name="combobox">The select, found by role and name, already showing <paramref name="option"/>.</param>
    /// <param name="option">The option's name, exactly.</param>
    /// <param name="testId">The select's test id, which focus must be inside.</param>
    public async Task ChooseAgainAsync(ILocator combobox, string option, string testId)
    {
        await ChooseAsync(combobox, option).ConfigureAwait(false);
        await Page.GetByRole(AriaRole.Option, new() { Name = option, Exact = true })
            .WaitForAsync(new() { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        await WaitForFocusInsideAsync(testId).ConfigureAwait(false);
    }

    /// <summary>Types a hook condition as CEL: switches the condition to text mode first, which the guided form is not.</summary>
    /// <remarks>
    /// One place for every scenario. The text box appears a round trip after the switch is pressed; the fill waits for it
    /// (<c>input#</c>, since the guided form's readout carries the same id).
    /// </remarks>
    /// <param name="condition">The CEL.</param>
    public async Task TypeConditionAsync(string condition)
    {
        await Page.GetByTestId("hook-condition-mode").GetByRole(AriaRole.Radio, new() { Name = "Text", Exact = true })
            .ClickAsync().ConfigureAwait(false);
        await Page.FillAsync("input#hook-condition", condition).ConfigureAwait(false);
    }

    /// <summary>Waits for the snackbar that says <paramref name="text"/>.</summary>
    /// <remarks>
    /// The library's own class is the handle: a snackbar is a <c>role=status</c> (admin.js turns the library's alert
    /// into one), and so is an info or success alert in place, and the difference between those is exactly what a
    /// scenario asserts (spec §3.3). This is the one library class the suite names, here and nowhere in a scenario.
    /// </remarks>
    /// <param name="text">What it says, or part of it.</param>
    public Task SnackbarAsync(string text)
        => Snackbars.Filter(new() { HasText = text }).First.WaitForAsync();

    /// <summary>Every snackbar on screen, by the library's class for <see cref="SnackbarAsync"/>'s reason.</summary>
    public ILocator Snackbars => Page.Locator(".mud-snackbar");

    /// <summary>
    /// The underline of the tab strip that holds <paramref name="tab"/>, by the library's class for
    /// <see cref="SnackbarAsync"/>'s reason: the slider is a drawing with no role or name of its own.
    /// </summary>
    /// <param name="tab">A tab of the strip.</param>
    /// <returns>The strip's slider.</returns>
    public static ILocator SliderOf(ILocator tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.Locator("xpath=ancestor::*[contains(@class,'mud-tabs-tabbar')][1]").Locator(".mud-tab-slider");
    }

    /// <summary>How many snackbars are on screen, or how many say <paramref name="text"/>.</summary>
    /// <remarks>
    /// For the two things a count proves and a wait cannot: an action that ran once said so once, and an error
    /// said nothing in a snackbar at all. The same library class as <see cref="SnackbarAsync"/>, for its reason.
    /// </remarks>
    /// <param name="text">What they say, or part of it; every snackbar when <see langword="null"/>.</param>
    /// <returns>The count.</returns>
    public Task<int> SnackbarCountAsync(string? text = null)
        => (text is null ? Snackbars : Snackbars.Filter(new() { HasText = text })).CountAsync();

    /// <summary>The focused element, as <c>tag#id[test id]</c>, for the focus rules of spec §3.4.</summary>
    /// <returns>A short description of <c>document.activeElement</c>.</returns>
    public Task<string> FocusedAsync()
        => Page.EvaluateAsync<string>(
            "() => { const e = document.activeElement; if (!e) return '';"
            + " return `${e.tagName.toLowerCase()}#${e.id}[${e.getAttribute('data-testid') ?? ''}]`; }");

    /// <summary>
    /// Waits for <paramref name="element"/> to be in view: its box inside the visible rectangle of the nearest
    /// ancestor that scrolls, or of the window when none does (spec §3.5, a new item is scrolled to).
    /// </summary>
    /// <param name="element">The element, which must be attached.</param>
    public async Task WaitForInViewAsync(ILocator element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var handle = await element.ElementHandleAsync().ConfigureAwait(false);
        await Page.WaitForFunctionAsync(
            """
            el => {
              const box = el.getBoundingClientRect();
              let top = 0, bottom = window.innerHeight;
              for (let p = el.parentElement; p; p = p.parentElement) {
                const style = getComputedStyle(p);
                if (/(auto|scroll)/.test(style.overflowY) && p.scrollHeight > p.clientHeight) {
                  const frame = p.getBoundingClientRect();
                  top = Math.max(top, frame.top);
                  bottom = Math.min(bottom, frame.bottom);
                  break;
                }
              }
              return box.height > 0 && box.top >= top - 1 && box.bottom <= bottom + 1;
            }
            """, handle, _polling).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until, after <paramref name="confirm"/> closed, the focused element matches <paramref name="selector"/>, and
    /// is never <c>&lt;body&gt;</c> (spec §3.2).
    /// </summary>
    /// <remarks>
    /// <paramref name="confirm"/> names the confirm by its test id (<c>discard-sheet@PendingBar</c> for the one confirm
    /// drawn in two places), and <c>PatternLanguageTests</c> reads those names to fail a confirm no scenario pins, for
    /// its Cancel and its verb both.
    /// </remarks>
    /// <param name="confirm">The confirm whose close this follows.</param>
    /// <param name="selector">Where focus must be.</param>
    public async Task FocusAfterConfirmAsync(string confirm, string selector)
    {
        try
        {
            await Page.WaitForFunctionAsync(
                "selector => document.activeElement !== document.body && !!document.activeElement?.matches(selector)",
                selector, _polling).ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException(
                $"After {confirm} closed, focus was on {await FocusedAsync().ConfigureAwait(false)}, not {selector}.", timeout);
        }
    }

    /// <summary>Waits until focus is inside the element with <paramref name="testId"/>, or inside the dialog around it.</summary>
    /// <remarks>
    /// <para>
    /// One wait for both questions (it was two helpers): <see cref="FocusScope.Element"/> for a panel or a control that
    /// must take focus itself — an error panel inside a sheet is <i>not</i> answered by focus elsewhere in the sheet — and
    /// <see cref="FocusScope.Dialog"/> for a dialog that just opened, whose test id the library may put on an element
    /// inside it that never holds focus (a confirm's does; measured).
    /// </para>
    /// <para>
    /// Call it with <see cref="FocusScope.Dialog"/> before a key meant for a dialog that just opened. Being visible does not
    /// mean the dialog is ready for keys: it takes focus a render later, and an Escape pressed before then goes to the page
    /// and closes nothing. That race made <c>CreateActionScenarios</c> flaky. Focus on <c>&lt;body&gt;</c> is never inside.
    /// </para>
    /// </remarks>
    /// <param name="testId">The element's test id.</param>
    /// <param name="scope">Whether focus must be inside that element, or anywhere in the dialog around it.</param>
    public Task WaitForFocusInsideAsync(string testId, FocusScope scope = FocusScope.Element)
        => Page.WaitForFunctionAsync(
            """
            ([id, dialog]) => {
              const focused = document.activeElement;
              if (!focused || focused === document.body) return false;
              const marked = `[data-testid='${id}']`;
              return dialog
                ? [...document.querySelectorAll(marked)].some(m => !!m.closest("[role='dialog']")?.contains(focused))
                : !!focused.closest(marked);
            }
            """,
            new object[] { testId, scope == FocusScope.Dialog }, _polling);

    /// <summary>Waits until the element with <paramref name="id"/> has focus.</summary>
    /// <param name="id">The element's id.</param>
    public Task WaitForFocusOnAsync(string id)
        => Page.WaitForFunctionAsync("id => document.activeElement?.id === id", id, _polling);

    /// <summary>Waits until the element with <paramref name="testId"/> has focus.</summary>
    /// <param name="testId">The element's test id.</param>
    public Task WaitForFocusOnTestIdAsync(string testId)
        => Page.WaitForFunctionAsync("id => document.activeElement?.dataset.testid === id", testId, _polling);

    /// <summary>The theme the page is drawn in: alvo.js always writes a resolved one before paint.</summary>
    /// <returns><c>light</c> or <c>dark</c>.</returns>
    public Task<string> ThemeAsync()
        => Page.EvaluateAsync<string>(
            "() => document.documentElement.dataset.theme ?? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')");

    /// <summary>The element's box once nothing on the page is moving.</summary>
    /// <remarks>
    /// A pane slides in and the page beside it narrows with it; a box read mid-slide is the animation's, not the
    /// layout's. Every running animation is waited out rather than a guessed number of milliseconds.
    /// </remarks>
    /// <param name="element">The element, which must be laid out.</param>
    /// <returns>Its box.</returns>
    public static async Task<LocatorBoundingBoxResult> SettledBoxAsync(ILocator element)
    {
        ArgumentNullException.ThrowIfNull(element);
        await element.Page.WaitForFunctionAsync(
            "() => document.getAnimations().every(a => a.playState !== 'running')", null, _polling).ConfigureAwait(false);
        return await element.BoundingBoxAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("the element is not laid out");
    }

    /// <summary>Stages a new entity, plans it and applies it; answers the revision it became.</summary>
    /// <param name="name">The entity's name.</param>
    /// <returns>The revision, as the success panel says it.</returns>
    public async Task<string> ApplyNewEntityAsync(string name)
    {
        await GoAsync("/schema").ConfigureAwait(false);
        await Button("New entity", exact: true).ClickAsync().ConfigureAwait(false);
        await Page.FillAsync("#new-entity-name", name).ConfigureAwait(false);
        await Page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        await Page.WaitForAddressAsync($"**/schema/{name}").ConfigureAwait(false);
        await PreviewPendingAsync().ConfigureAwait(false);
        await Page.FillAsync("#apply-reason", $"Add {name}").ConfigureAwait(false);
        await Button("Apply these changes").ClickAsync().ConfigureAwait(false);
        var announced = Content.GetByText("Applied as revision").First;
        await announced.WaitForAsync().ConfigureAwait(false);
        return System.Text.RegularExpressions.Regex.Match(
            await announced.InnerTextAsync().ConfigureAwait(false), @"revision (\d+)").Groups[1].Value;
    }

    /// <summary>Creates a person from the Access screen it is on, and answers the id their row carries.</summary>
    /// <param name="email">Who to create.</param>
    /// <returns>The person's id.</returns>
    public async Task<string> CreatePersonAsync(string email)
    {
        await Page.GetByTestId("person-new").ClickAsync().ConfigureAwait(false);
        await Dialog("person-create").WaitForAsync().ConfigureAwait(false);
        await Page.FillAsync("#new-person-email", email).ConfigureAwait(false);
        await Page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        await SnackbarAsync($"Created {email}").ConfigureAwait(false);
        return await PersonIdAsync(email).ConfigureAwait(false);
    }

    /// <summary>Opens a person's editor from their row's Edit.</summary>
    /// <param name="id">The person's id, as their row carries it.</param>
    /// <returns>The editor.</returns>
    public async Task<ILocator> OpenPersonAsync(string id)
    {
        await Page.Locator($"#change-{id}").ClickAsync().ConfigureAwait(false);
        var editor = Dialog("person-editor");
        await editor.WaitForAsync().ConfigureAwait(false);
        return editor;
    }

    /// <summary>The id of the person whose row names <paramref name="email"/>, off the row's own <c>id</c>.</summary>
    /// <remarks>
    /// Not a row located by its text: rows nest inside the panel, so a text-scoped locator can match an ancestor whose
    /// Change belongs to somebody else.
    /// </remarks>
    /// <param name="email">Whose row.</param>
    /// <returns>The uuid in the row's id.</returns>
    public async Task<string> PersonIdAsync(string email)
    {
        var id = await Page.Locator("[id^='person-']", new() { HasText = email }).First
            .GetAttributeAsync("id").ConfigureAwait(false);
        id.ShouldNotBeNull($"no person row carries {email}");
        return id["person-".Length..];
    }

    /// <summary>Whether focus is inside the element with <paramref name="testId"/>.</summary>
    /// <param name="testId">The container's test id.</param>
    /// <returns><see langword="true"/> when the focused element is it or inside it.</returns>
    public Task<bool> FocusIsInsideAsync(string testId)
        => Page.EvaluateAsync<bool>(
            "id => !!document.activeElement?.closest(`[data-testid='${id}']`)", testId);

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
    /// plan rather than the URL alone, because the URL moves before the screen behind it does.
    /// </remarks>
    public async Task PreviewPendingAsync()
    {
        await Page.ClickAsync("[data-testid='pending-preview']").ConfigureAwait(false);
        try
        {
            await Page.WaitForAddressAsync("**/changes").ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            var links = await Page.Locator("[data-testid='pending-preview']").CountAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"Preview never reached /changes; the page is at {Page.Url}, {links} preview link(s), focus {await FocusedAsync().ConfigureAwait(false)}, "
                + $"console: {string.Join(" | ", _noise)}", timeout);
        }

        await WaitForPlanAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the plan Preview asks for on arrival — a dry run, so the screen runs it without a click.
    /// </summary>
    public Task WaitForPlanAsync() => Page.GetByTestId("plan").WaitForAsync();

    /// <summary>Opens Import and waits until it has loaded the working copy an import replaces.</summary>
    /// <remarks>
    /// The circuit being up is not enough: the page reads the applied descriptor and the copy after its first render, and
    /// it refuses an import until both are there (<c>ImportGate</c>). A chord sent in between is refused without a word —
    /// a disabled button is waited on by a click, a key press is not. The form draws the gate's condition, so this waits on
    /// exactly what the submit checks. It relies on <see cref="GoAsync"/> settling first: the page is prerendered, so the
    /// HTML that arrives before the circuit can already read <c>data-copy-loaded='true'</c>, and only the circuit being up
    /// makes that attribute the interactive page's own.
    /// </remarks>
    public async Task GoToImportAsync()
    {
        await GoAsync("/transfer").ConfigureAwait(false);
        await Page.Locator("form[data-copy-loaded='true']").WaitForAsync().ConfigureAwait(false);
    }

    /// <summary>Opens Import, pastes <paramref name="text"/>, submits it with the chord, and waits for its plan's URL.</summary>
    /// <remarks>
    /// The box is read back before the key: <c>InputValueAsync</c> is a browser-local read, so it proves the fill landed
    /// in the box the chord submits, and leaves only the circuit's side to the wait.
    /// </remarks>
    /// <param name="text">The descriptor to import.</param>
    public async Task ImportByChordAsync(string text)
    {
        await GoToImportAsync().ConfigureAwait(false);
        await Page.FillAsync("#import-json", text).ConfigureAwait(false);
        (await Page.InputValueAsync("#import-json").ConfigureAwait(false)).ShouldBe(text);
        await Page.Locator("#import-json").PressAsync("Meta+Enter").ConfigureAwait(false);
        await WaitForImportedAsync().ConfigureAwait(false);
    }

    /// <summary>Waits for an import to reach its plan's URL, and says what Import showed when it did not.</summary>
    public async Task WaitForImportedAsync()
    {
        try
        {
            await Page.WaitForAddressAsync("**/changes").ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            throw new InvalidOperationException(await ImportStateAsync().ConfigureAwait(false), timeout);
        }
    }

    /// <summary>What Import shows, for a wait that timed out on it: the box, its hint, any refusal, and the question.</summary>
    private async Task<string> ImportStateAsync()
    {
        static async Task<string> TextOf(ILocator locator)
            => await locator.CountAsync().ConfigureAwait(false) == 0 ? "none" : await locator.First.InnerTextAsync().ConfigureAwait(false);

        var box = await Page.Locator("#import-json").CountAsync().ConfigureAwait(false) == 0
            ? "absent"
            : await Page.InputValueAsync("#import-json").ConfigureAwait(false);
        var shown = box.Length > 200 ? $"{box[..200]}… ({box.Length} characters)" : box;
        var hint = await TextOf(Page.Locator("#import-json-hint")).ConfigureAwait(false);
        var error = await TextOf(Page.GetByTestId("error-panel")).ConfigureAwait(false);
        var asking = await Page.GetByTestId("import-replace-confirm").CountAsync().ConfigureAwait(false) > 0;
        return $"The import never reached /changes; the page is at {Page.Url}. Box: {shown} | Hint: {hint} | Error panel: {error} "
            + $"| Replace question open: {asking} | Console: {(_noise.Count == 0 ? "nothing" : string.Join(" | ", _noise))}";
    }

    /// <summary>
    /// Opens one of an entity's tabs and waits for it to actually be the open one.
    /// </summary>
    /// <remarks>
    /// The click re-renders over the circuit, so reading the panel straight afterwards reads the
    /// tab that was open before. Waiting on the tab being the selected one is waiting on the thing the
    /// click was for.
    /// </remarks>
    /// <param name="tab">The tab's label.</param>
    public async Task OpenTabAsync(string tab)
    {
        await Page.GetByRole(AriaRole.Tab, new() { Name = tab, Exact = true }).ClickAsync().ConfigureAwait(false);
        await Page.GetByRole(AriaRole.Tab, new() { Name = tab, Exact = true, Selected = true })
            .WaitForAsync().ConfigureAwait(false);
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

        /* And wait for the shell, which the keyboard does NOT imply: the keyboard is the palette's first render and
           the popover and dialog providers are the layout's. A select opened between the two opened nothing. */
        try
        {
            await Page.WaitForFunctionAsync(
                "() => document.documentElement.dataset.alvoShell === 'ready'", null, _polling).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"The shell at {Page.Url} never said it was ready, so its popover and dialog providers may not be live. "
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

    /// <summary>
    /// Fails when the open editor sheet scrolls sideways, or reaches past the window's edge.
    /// </summary>
    /// <remarks>
    /// <see cref="AssertNoHorizontalScrollAsync"/> measures the document and <c>main.a-content</c>. A sheet is a dialog
    /// outside both, with its own scroll container (<c>.a-editor__body</c>, <c>overflow: auto</c>), so content wider
    /// than a phone scrolled the sheet while that check stayed green (spec §9.5). It fails, too, when no sheet is open:
    /// a check that measured nothing must not pass.
    /// </remarks>
    public async Task AssertSheetFitsAsync()
    {
        await SettleAsync().ConfigureAwait(false);
        var overflow = await Page.EvaluateAsync<int>(
            "() => { const body = document.querySelector('.a-editor__body'), sheet = document.querySelector('.a-editor');"
            + " if (!body || !sheet) return -1;"
            + " return Math.max(body.scrollWidth - body.clientWidth,"
            + "   Math.ceil(sheet.getBoundingClientRect().right - window.innerWidth)); }").ConfigureAwait(false);

        overflow.ShouldNotBe(-1, "no editor sheet is open, so there was nothing to measure");
        overflow.ShouldBeLessThanOrEqualTo(1, "the editor sheet scrolls sideways at this width");
    }

    /// <summary>Fails when the visible shell has nothing in it.</summary>
    /// <remarks>
    /// The cheap guard against the failure this suite exists for: a caught exception that renders
    /// the chrome and no content looks fine in a screenshot and is useless to an operator.
    /// </remarks>
    public async Task AssertRenderedAsync()
    {
        var text = await Content.InnerTextAsync().ConfigureAwait(false);
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
            /* First, so the trace ends on the screen as the scenario left it rather than on about:blank. */
            await ScenarioTraces.StopAsync(context).ConfigureAwait(false);

            try
            {
                await Page.GotoAsync("about:blank").ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                /* The page is already gone; there is nothing left to disconnect. */
            }
        }
        finally
        {
            await context.CloseAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }

}

/// <summary>What <see cref="AdminSession.WaitForFocusInsideAsync"/> asks focus to be inside.</summary>
public enum FocusScope
{
    /// <summary>The element with the test id itself.</summary>
    Element,

    /// <summary>The <c>role=dialog</c> around the element with the test id.</summary>
    Dialog,
}
