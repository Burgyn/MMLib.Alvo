using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Every button and link the dashboard draws reads at AA against what is behind it, in both themes (WCAG 1.4.3).
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
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task Every_button_and_link_reads_at_AA_on_the_screens_and_in_the_editors(ColorScheme scheme)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, colorScheme: scheme);
        await StageAFieldAsync(session);
        var failures = new List<string>();

        foreach (var route in new[] { "/schema", "/data/regions", "/access", "/history", "/settings" })
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

        await session.GoAsync("/data/regions");
        await session.Button("New record", exact: true).ClickAsync();
        await session.Dialog("record-sheet").WaitForAsync();
        failures.AddRange(await BelowAAAsync(session, "the record editor"));

        await session.GoAsync("/access");
        await session.Page.GetByTestId("person-new").ClickAsync();
        await session.Dialog("person-create").WaitForAsync();
        failures.AddRange(await BelowAAAsync(session, "the new person editor"));

        failures.ShouldBeEmpty();
    }

    private static async Task StageAFieldAsync(AdminSession session)
    {
        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.Locator("#new-field-name").FillAsync("contrast_probe");
        await session.Page.GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId("pending-bar").WaitForAsync();
    }

    private static async Task<IEnumerable<string>> BelowAAAsync(AdminSession session, string where)
        => (await ContrastProbe.ReadAsync(ContrastProbe.Controls(session.Page)))
            .Where(reading => reading.Ratio < ContrastProbe.AA)
            .Select(reading => $"{where}: {reading}");
}
