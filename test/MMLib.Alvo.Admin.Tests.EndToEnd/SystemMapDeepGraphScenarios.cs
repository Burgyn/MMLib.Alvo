using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The system map over a deep graph with automation: <c>examples/complex-crm</c>, imported into the working
/// copy, is five ref layers deep and its automation starts at <c>deals</c>, two columns short of the outside one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own world, one fact per class</b>: the import replaces this operator's working copy, which every other
/// scenario sharing a world would then draw instead of field-service, and a second import over the first would be
/// asked about first (spec §3.2). One import per world is what makes the import's answer known in advance.
/// </para>
/// <para>
/// <b>The drawing order is the assertion</b>, because the defect it pins was one of paint: a wire drawn before
/// the boxes ran under <c>invoices</c> and <c>invoice_items</c> and came out of the last one's edge, reading as
/// its wire. Pixels are not checkable here; which element the svg paints last is.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class SystemMapDeepGraphScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Automation_wires_are_painted_over_the_boxes_and_say_not_yet()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await ImportCrmAsync(session);

        await session.GoAsync("/schema?view=map&layers=reactions");
        var map = session.Page.GetByTestId("system-map");
        await map.GetByRole(AriaRole.Link, new() { Name = "invoice_items", Exact = false }).WaitForAsync();

        (await PaintedAfterEveryBoxAsync(map)).ShouldBeTrue("every reaction wire is drawn after the last box");
        (await map.GetByTestId("map-reaction-word").AllTextContentsAsync()).ShouldBe(["not yet", "not yet"]);
        (await map.TextContentAsync() ?? string.Empty).ShouldContain("deals posts to invoicing on automation deal-won");
        session.AssertConsoleClean();
    }

    /// <summary>Loads complex-crm into this operator's clean working copy through the Import box.</summary>
    /// <remarks>
    /// The world's copy is clean, so nothing is asked: the import goes straight to Preview. A question here would mean
    /// a scenario shared the world after all.
    /// </remarks>
    internal static async Task ImportCrmAsync(AdminSession session)
    {
        await session.GoToImportAsync();
        await session.Page.FillAsync("#import-json", Descriptors.ComplexCrm);
        await session.Button("Load it into the working copy").ClickAsync();

        await session.Page.WaitForURLAsync("**/changes**");
        (await session.Page.GetByTestId("import-replace-confirm").CountAsync()).ShouldBe(0, "a clean copy is not asked about");
    }

    /// <summary>Whether, in document order inside the svg, every reaction wire comes after every entity box.</summary>
    private static Task<bool> PaintedAfterEveryBoxAsync(ILocator map)
        => map.EvaluateAsync<bool>("""
            map => {
              const painted = [...map.querySelectorAll('svg a, svg [data-testid="map-reaction"]')];
              const lastBox = painted.map(e => e.localName).lastIndexOf('a');
              const firstWire = painted.findIndex(e => e.dataset.testid === 'map-reaction');
              return lastBox >= 0 && firstWire > lastBox;
            }
            """);
}

/// <summary>
/// What the map over complex-crm cannot draw is said, and what nothing uses is faint; its own world for
/// <see cref="SystemMapDeepGraphScenarios"/>' reason.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class SystemMapUndrawnScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// crm's two other rules — one on a schedule, one that only makes an <c>http.call</c> — draw nothing, and
    /// the panel says so; the template no rule or hook uses is drawn faint.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task What_the_map_cannot_draw_is_said_and_what_nothing_uses_is_faint()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await SystemMapDeepGraphScenarios.ImportCrmAsync(session);

        await session.GoAsync("/schema?view=map&layers=reactions");
        var map = session.Page.GetByTestId("system-map");
        await map.WaitForAsync();

        (await session.Page.GetByTestId("map-undrawn").TextContentAsync() ?? string.Empty)
            .ShouldStartWith("2 automation rules draw no wire");
        var spare = map.GetByTestId("map-node").Filter(new() { HasText = "invoice-issued" });
        (await spare.GetAttributeAsync("class") ?? string.Empty).ShouldContain("a-map__outside--unused");
        (await map.GetByTestId("map-node").Filter(new() { HasText = "invoicing" }).GetAttributeAsync("class") ?? string.Empty)
            .ShouldNotContain("unused");
        session.AssertConsoleClean();
    }
}
