using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The library is loaded beneath Alvo's rules, and both themes are right on the first paint (spec D8, D9).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class FoundationScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /* The library writes its palette variables as rgba(), not as the hex it was given: these are --accent's
       #0f7a48 and #39e991, which AlvoMudThemeTests pins equal to alvo.css. */
    private const string LightAccent = "rgba(15,122,72,1)";
    private const string DarkAccent = "rgba(57,233,145,1)";

    /* --panel's dark value, #262833, as a painted colour. */
    private const string DarkPanel = "rgb(38, 40, 51)";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_library_stylesheet_is_loaded_into_the_lowest_layer_and_Alvos_spacing_survives_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        var layered = await session.Page.EvaluateAsync<bool>(
            "() => [...document.styleSheets].flatMap(s => { try { return [...s.cssRules]; } catch { return []; } })"
            + ".some(r => r instanceof CSSImportRule && r.layerName === 'mud' && r.styleSheet?.cssRules.length > 0)");
        layered.ShouldBeTrue("MudBlazor.min.css must arrive through alvo-mud.css, inside @layer mud");

        /* Mud's reset is *{padding:0}; the content pane's padding is an Alvo rule it must not beat. */
        (await session.Content.EvaluateAsync<string>("e => getComputedStyle(e).paddingTop")).ShouldNotBe("0px");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Referencing the library moves nothing Alvo draws: every element that is not Mud's computes the same style
    /// with the library's sheet as without it (spec D8).
    /// </summary>
    /// <remarks>
    /// The comparison covers every element. The named tags are the kinds Mud's reset was seen to strip: a
    /// section's subtitle paragraph, a code block, a heading and a list. Naming them makes a screen that stopped
    /// rendering them fail rather than pass. They are tags rather than classes because the suite does not add
    /// raw design-system selectors (EndToEndSelectorTests).
    /// </remarks>
    /// <param name="path">The screen.</param>
    /// <param name="named">Elements the screen must show, comma-separated.</param>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("", "h1")]
    [InlineData("/welcome", "h1,p")]
    [InlineData("/automations", "p")]
    [InlineData("/access", "h1")]
    [InlineData("/schema", "h1")]
    [InlineData("/schema?view=map", "ul")]
    [InlineData("/schema/work_orders", "h1")]
    [InlineData("/data/regions", "h1")]
    [InlineData("/changes", "h1")]
    [InlineData("/history", "h1")]
    [InlineData("/rules", "h1")]
    [InlineData("/transfer", "h1,pre")]
    [InlineData("/settings", "h1")]
    public async Task The_library_changes_no_style_of_an_element_Alvo_draws(string path, string named)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync(path);

        var reading = await LibraryProbe.ReadAsync(session.Page, named.Split(','));

        reading.Missing.ShouldBeEmpty($"{path} must show these for the comparison to cover them");
        reading.Compared.ShouldBeGreaterThan(20);
        reading.Differences.ShouldBeEmpty($"MudBlazor's sheet restyles Alvo's markup on '{path}'");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_stored_dark_theme_is_dark_on_the_first_paint_before_the_circuit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");
        await session.Page.EvaluateAsync("() => localStorage.setItem('alvo.theme', 'dark')");

        await session.Page.GotoAsync(session.Page.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        (await session.ThemeAsync()).ShouldBe("dark");
        (await PrimaryAsync(session)).ShouldStartWith(DarkAccent);
        await session.SettleAsync();
        (await PrimaryAsync(session)).ShouldStartWith(DarkAccent);
        await session.Page.EvaluateAsync("() => localStorage.removeItem('alvo.theme')");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task With_nothing_stored_the_theme_is_the_systems_and_follows_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await session.GoAsync("");

        (await session.ThemeAsync()).ShouldBe("dark");
        (await PrimaryAsync(session)).ShouldStartWith(DarkAccent);

        await session.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await session.Page.WaitForFunctionAsync("() => document.documentElement.dataset.theme === 'light'");
        (await PrimaryAsync(session)).ShouldStartWith(LightAccent);
    }

    /// <summary>
    /// A browser that was dark from its first request, with nothing stored, paints the library's surfaces dark: the
    /// app bar and the nav drawer are the dark <c>--panel</c> on the first paint and still after the circuit is up.
    /// </summary>
    /// <remarks>
    /// Painted colours rather than the palette variable: the variable can be right while a surface reads another
    /// one, and a surface is what the operator sees. Without the dark-scoped theme provider both are white.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_dark_system_paints_the_app_bar_and_drawer_dark_with_nothing_stored()
    {
        await using var session = await world.SignInAsync(
            TestContext.Current.CancellationToken, colorScheme: ColorScheme.Dark);
        await session.GoAsync("");
        (await session.Page.EvaluateAsync<string?>("() => localStorage.getItem('alvo.theme')")).ShouldBeNull();

        await AdminSession.WithoutTheCircuitAsync(session.Page, () => session.Page.ReloadAsync());
        await AssertDarkSurfacesAsync(session, "on the first paint");

        await session.Page.ReloadAsync();
        await session.SettleAsync();
        await AssertDarkSurfacesAsync(session, "after the circuit");
        session.AssertConsoleClean();
    }

    private static async Task AssertDarkSurfacesAsync(AdminSession session, string when)
    {
        foreach (var surface in new[] { "appbar", "sidebar" })
        {
            var painted = await session.Page.GetByTestId(surface).EvaluateAsync<string>(
                "e => getComputedStyle(e.closest('aside') ?? e).backgroundColor");
            painted.ShouldBe(DarkPanel, $"the {surface} {when}");
        }
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_theme_toggle_flips_the_library_palette_with_no_round_trip()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await session.GoAsync("");
        (await PrimaryAsync(session)).ShouldStartWith(LightAccent);

        await session.Page.GetByTestId("theme-toggle").ClickAsync();

        await session.Page.WaitForFunctionAsync("() => document.documentElement.dataset.theme === 'dark'");
        (await PrimaryAsync(session)).ShouldStartWith(DarkAccent);
        await session.Page.EvaluateAsync("() => localStorage.removeItem('alvo.theme')");
    }

    private static async Task<string> PrimaryAsync(AdminSession session)
        => (await session.Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-primary')")).Trim().ToLowerInvariant();
}
