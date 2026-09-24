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

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_stored_dark_theme_is_dark_on_the_first_paint_before_the_circuit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");
        await session.Page.EvaluateAsync("() => localStorage.setItem('alvo.theme', 'dark')");

        await session.Page.GotoAsync(session.Page.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        (await ThemeAsync(session)).ShouldBe("dark");
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

        (await ThemeAsync(session)).ShouldBe("dark");
        (await PrimaryAsync(session)).ShouldStartWith(DarkAccent);

        await session.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await session.Page.WaitForFunctionAsync("() => document.documentElement.dataset.theme === 'light'");
        (await PrimaryAsync(session)).ShouldStartWith(LightAccent);
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

    private static Task<string> ThemeAsync(AdminSession session)
        => session.Page.EvaluateAsync<string>("() => document.documentElement.dataset.theme ?? ''");

    private static async Task<string> PrimaryAsync(AdminSession session)
        => (await session.Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-primary')")).Trim().ToLowerInvariant();
}
