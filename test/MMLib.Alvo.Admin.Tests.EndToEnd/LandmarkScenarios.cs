using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The shell reads the same to a screen reader on every screen and at every width (final review M15, M17): one name
/// per navigation landmark, no unnamed complementary one, a skip link to the content, a palette that is a combobox, a
/// snackbar that is a status, and a phone's drawer that is never drawn open while the circuit starts.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class LandmarkScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>Every navigation landmark has a name, and no two share one, on a desktop and on a phone with the drawer open.</summary>
    /// <remarks>Every screen with a pane of its own beside the list is walked, not only Schema (re-review N3).</remarks>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1400, "/schema")]
    [InlineData(390, "/schema")]
    [InlineData(1400, "/history")]
    [InlineData(1400, "/rules")]
    [InlineData(1400, "/data/regions")]
    [InlineData(390, "/access")]
    public async Task Each_navigation_landmark_is_named_once_and_no_region_is_unnamed(int width, string path)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync(path);
        if (width < 720)
        {
            await session.Page.GetByTestId("more-sections").ClickAsync();
            await session.Page.GetByTestId("sidebar").WaitForAsync();
        }

        var names = await LandmarkNamesAsync(session, "nav");
        names.ShouldNotContain(string.Empty, "every navigation landmark is named");
        names.Distinct().Count().ShouldBe(names.Count, $"two navigation landmarks share a name: {string.Join(", ", names)}");
        (await LandmarkNamesAsync(session, "aside")).ShouldNotContain(string.Empty, "an aside is a region, so it is named");
    }

    /// <summary>The first Tab reaches the skip link, and it moves focus to the screen's content (WCAG 2.4.1).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_first_Tab_is_a_skip_link_to_the_content()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        /* First in the tab order: the first element of the document that Tab can reach. */
        (await session.Page.EvaluateAsync<string?>(
            """
            () => [...document.querySelectorAll('a[href], button, input, select, textarea, [tabindex]')]
              .find(e => e.tabIndex >= 0 && !e.disabled && e.getClientRects().length > 0)?.dataset.testid ?? null
            """)).ShouldBe("skip-to-content");

        await session.Page.GetByTestId("skip-to-content").FocusAsync();
        (await session.Page.GetByTestId("skip-to-content").IsVisibleAsync()).ShouldBeTrue("it is drawn once it has focus");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.WaitForFocusOnAsync("a-content");
    }

    /// <summary>The palette's box is a combobox over its list, and the arrows move the option it names as active.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_palette_is_a_combobox_whose_arrows_move_the_active_option()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Page.Keyboard.PressAsync("Control+k");

        var box = session.Page.GetByRole(AriaRole.Combobox, new() { Name = "Search" });
        await box.WaitForAsync();
        var list = await box.GetAttributeAsync("aria-controls");
        (await session.Page.Locator($"#{list}").GetAttributeAsync("role")).ShouldBe("listbox");
        var first = await box.GetAttributeAsync("aria-activedescendant");

        await session.Page.Keyboard.PressAsync("ArrowDown");
        await session.Page.WaitForFunctionAsync(
            "first => document.activeElement?.getAttribute('aria-activedescendant') !== first", first);
        var active = await box.GetAttributeAsync("aria-activedescendant");
        (await session.Page.Locator($"#{active}").GetAttributeAsync("aria-selected")).ShouldBe("true");
        (await box.EvaluateAsync<bool>("b => document.activeElement === b")).ShouldBeTrue("focus stays in the box");
    }

    /// <summary>
    /// A snackbar is read out by the one polite region it appears in, which is on the page, empty, before any message
    /// (spec §3.3; batch-B re-review N2): a region inserted with its content is often not announced at all.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_snackbar_appears_in_the_polite_region_that_was_there_first()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        var region = session.Page.GetByTestId("snackbars");
        (await region.GetAttributeAsync("role")).ShouldBe("status");
        (await region.GetAttributeAsync("aria-live")).ShouldBe("polite");
        (await region.InnerTextAsync()).Trim().ShouldBeEmpty("the region is there before any message");

        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.FillAsync("#new-field-name", "status_note");
        await session.Page.Keyboard.PressAsync("Enter");

        await session.SnackbarAsync("added to the working copy");
        (await region.InnerTextAsync()).ShouldContain("added to the working copy", Case.Sensitive, "the message is in the region");
        (await session.Snackbars.EvaluateAllAsync<string[]>("bars => bars.map(bar => bar.getAttribute('role') ?? '')"))
            .ShouldAllBe(role => role == string.Empty, "a snackbar is no region of its own inside the one that reads it");
    }

    /// <summary>
    /// A phone's drawer is never drawn open, not on the server's first paint (<c>ShellScenarios</c>) and not while the
    /// circuit's first render learns the width: it used to slide out from the desktop's open position (M17).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_phones_drawer_is_never_drawn_while_the_circuit_starts()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 390);
        await session.Page.AddInitScriptAsync("""
            (() => {
              window.__alvoDrawerSeen = false;
              const look = () => {
                const nav = document.querySelector("[data-testid='sidebar']");
                if (nav) {
                  const style = getComputedStyle(nav);
                  const box = nav.getBoundingClientRect();
                  if (style.visibility !== 'hidden' && style.display !== 'none' && box.width > 0 && box.right > 1) {
                    window.__alvoDrawerSeen = true;
                  }
                }
              };
              setInterval(look, 5);
            })();
            """);
        await session.GoAsync("/schema");
        await session.Page.WaitForTimeoutAsync(500);

        (await session.Page.EvaluateAsync<bool>("() => window.__alvoDrawerSeen")).ShouldBeFalse(
            "the drawer was drawn over the page while the circuit started");
    }

    /// <summary>The accessible names of every rendered, not hidden, element of <paramref name="tag"/>.</summary>
    private static async Task<List<string>> LandmarkNamesAsync(AdminSession session, string tag)
        => [.. await session.Page.EvaluateAsync<string[]>(
            """
            tag => [...document.querySelectorAll(tag)]
              .filter(e => e.getAttribute('role') !== 'none' && e.getAttribute('role') !== 'presentation')
              .filter(e => { const s = getComputedStyle(e); return s.display !== 'none' && s.visibility !== 'hidden'; })
              .map(e => (e.getAttribute('aria-label') ?? '').trim())
            """, tag)];
}
