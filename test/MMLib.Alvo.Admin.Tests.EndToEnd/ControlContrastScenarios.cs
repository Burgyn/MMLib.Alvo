using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Every button and link the dashboard draws reads at AA against what is behind it, in both themes (WCAG 1.4.3), and so
/// do the two messages whose colours are the palette's tones: a success snackbar and an information alert.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured across screens, because the defect was never one button's.</b> The maintainer's "unreadable green"
/// was a base-layer <c>color: inherit</c> that beat the library's contrast text on every filled button, and then
/// the same rule for links on every filled button that is a link (the pending bar's Preview). A check on one
/// button proves one button.
/// </para>
/// <para>
/// Its own world, for <c>PendingWorkScenarios</c>' reason: it stages a field, so the pending bar is on screen.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ControlContrastScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /* Every screen with a control of its own, the read-only ones included: a filled link on the overview's banner is a
       filled button too. */
    private static readonly string[] _screens =
        ["", "/welcome", "/schema", "/data/regions", "/access", "/history", "/integrations", "/automations", "/settings"];

    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task Every_button_and_link_reads_at_AA_on_the_screens_and_in_the_editors(ColorScheme scheme)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, colorScheme: scheme);
        await StageAFieldAsync(session, scheme);
        var failures = new List<string>(await SnackbarBelowAAAsync(session));
        failures.AddRange(await SignInBelowAAAsync(scheme));
        failures.AddRange(await BadgesBelowAAAsync(session));
        failures.AddRange(await InfoAlertBelowAAAsync(session));

        foreach (var route in _screens)
        {
            await session.GoAsync(route);
            await session.Page.GetByTestId("pending-bar").WaitForAsync();
            failures.AddRange(await BelowAAAsync(session, route));
        }

        /* The library's one icon button: the filter's clear, drawn once the filter holds text. */
        await session.GoAsync("/schema");
        await session.Page.Locator("#entity-filter").FillAsync("reg");
        var clear = session.Page.Locator("#entity-filter").Locator("..").GetByRole(AriaRole.Button);
        await clear.WaitForAsync();
        failures.AddRange((await ContrastProbe.ReadAsync(clear)).Where(r => r.Ratio < ContrastProbe.AA)
            .Select(r => $"the filter's clear: {r}"));

        /* The row the cursor is on, drawn on the accent wash: the row's Edit is read against the wash, not the panel. */
        await SeedARegionAsync(scheme);
        await session.GoAsync("/data/regions");
        await session.Page.GetByTestId("grid-row").First.WaitForAsync();
        await session.Page.Keyboard.PressAsync("j");
        var selected = session.Page.GetByRole(AriaRole.Row, new() { Selected = true });
        await selected.WaitForAsync();
        failures.AddRange((await ContrastProbe.ReadAsync(selected.GetByRole(AriaRole.Button)))
            .Where(r => r.Ratio < ContrastProbe.AA).Select(r => $"the selected row: {r}"));

        await session.Button("New record", exact: true).ClickAsync();
        await session.Dialog("record-sheet").WaitForAsync();
        failures.AddRange(await BelowAAAsync(session, "the record editor"));

        await session.GoAsync("/access");
        await session.Page.GetByTestId("person-new").ClickAsync();
        await session.Dialog("person-create").WaitForAsync();
        failures.AddRange(await BelowAAAsync(session, "the new person editor"));

        failures.ShouldBeEmpty();
    }

    /// <summary>
    /// Two badges, which are small text on a translucent wash of their own: the drawer's "Not yet" pill (neutral) and
    /// the overview's applied revision (ok). Both read at or under AA by pixel sampling until their inks moved.
    /// </summary>
    private static async Task<IEnumerable<string>> BadgesBelowAAAsync(AdminSession session)
    {
        await session.GoAsync("");
        await session.Page.GetByTestId("overview-revision").WaitForAsync();
        var badges = session.Page.GetByTestId("overview-revision").Or(session.Page.GetByTestId("nav-not-yet"));
        var readings = await ContrastProbe.ReadAsync(badges);
        readings.Count.ShouldBeGreaterThanOrEqualTo(3, "the revision badge and both Not yet pills");
        return readings.Where(r => r.Ratio < ContrastProbe.AA).Select(r => $"the badge: {r}");
    }

    /// <summary>
    /// An information alert's text, on its own tone: the "Not yet" panel of a warned section. Its colour is the
    /// library's info colour, which the palette takes from <c>--accentInk</c>, so a token move reaches it unmeasured
    /// unless this reads it.
    /// </summary>
    private static async Task<IEnumerable<string>> InfoAlertBelowAAAsync(AdminSession session)
    {
        await session.GoAsync("/automations");
        var alert = session.Page.GetByTestId("notyet-panel");
        await alert.WaitForAsync();
        /* Every element inside the alert frame, by its test id rather than the library's classes; the ones with no text
           (the icon, its paths) are dropped below, so what is judged is what is read. */
        var readings = await ContrastProbe.ReadAsync(alert.Locator("*"));
        readings.ShouldNotBeEmpty("the alert's title and its sentences are read");
        return readings.Where(r => r.Name.Length > 0 && r.Ratio < ContrastProbe.AA).Select(r => $"the info alert: {r}");
    }

    /// <summary>The static sign-in page's one button, in a context of its own, before anybody signs in.</summary>
    private async Task<IEnumerable<string>> SignInBelowAAAsync(ColorScheme scheme)
    {
        await using var context = await world.Browser.NewContextAsync(new() { ColorScheme = scheme });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.SignInPath}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).WaitForAsync();
        return (await ContrastProbe.ReadAsync(ContrastProbe.Controls(page)))
            .Where(reading => reading.Ratio < ContrastProbe.AA)
            .Select(reading => $"the sign-in page: {reading}");
    }

    /// <summary>
    /// A success snackbar's message and its close, on the success fill: the dark theme's close was white on bright green,
    /// 1.6:1, until the palette gave each filled tone its contrast colour.
    /// </summary>
    private static async Task<IEnumerable<string>> SnackbarBelowAAAsync(AdminSession session)
    {
        var snackbar = session.Snackbars.Filter(new() { HasText = "Saved to the working copy" }).First;
        await snackbar.WaitForAsync();
        var readings = (await ContrastProbe.ReadAsync(snackbar)).Concat(await ContrastProbe.ReadAsync(snackbar.GetByRole(AriaRole.Button)));
        return readings.Where(r => r.Ratio < ContrastProbe.AA).Select(r => $"the snackbar: {r}");
    }

    private async Task SeedARegionAsync(ColorScheme scheme)
    {
        using var scope = world.Services.CreateScope();
        await FieldServiceSeed.RegionAsync(
            scope.ServiceProvider.GetRequiredService<IAlvoData>(), AlvoContext.System(TenantId.New()), $"CONTRAST-{scheme}");
    }

    private static async Task StageAFieldAsync(AdminSession session, ColorScheme scheme)
    {
        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.Locator("#new-field-name").FillAsync($"contrast_probe_{scheme}".ToLowerInvariant());
        await session.Page.GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId("pending-bar").WaitForAsync();
    }

    private static async Task<IEnumerable<string>> BelowAAAsync(AdminSession session, string where)
        => (await ContrastProbe.ReadAsync(ContrastProbe.Controls(session.Page)))
            .Where(reading => reading.Ratio < ContrastProbe.AA)
            .Select(reading => $"{where}: {reading}");
}
