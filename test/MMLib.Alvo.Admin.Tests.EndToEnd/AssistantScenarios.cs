namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The assistant, driven — with a connection and without one.
/// </summary>
/// <remarks>
/// Its own world, and the reason is the working copy: a copy is held per operator, so two scenarios signing
/// in as the same administrator compose one document between them.
/// </remarks>
/// <param name="world">The running host and browser, with a scripted assistant in it.</param>
public sealed class AssistantScenarios(AssistantWorld world) : IClassFixture<AssistantWorld>
{
    /// <summary>
    /// A proposal reaches the database only through Preview, and carries what was asked for.
    /// </summary>
    /// <remarks>
    /// The whole design in one scenario: the drawer has no apply, the operator confirms on the screen they
    /// already know, and the history row says an assistant drafted it and who pressed the button.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_operator_asks_for_an_entity_and_applies_the_proposal()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        await session.Page.ClickAsync("[data-testid='assistant-launch']");
        await session.Page.FillAsync("#assistant-message", "add an invoices entity");
        await session.Page.ClickAsync("[data-testid='assistant-send']");

        await session.Page.Locator("[data-testid='assistant-proposal']").WaitForAsync();

        /* The tools it called are shown by name, so a reader can see which questions it asked. */
        (await session.Page.Locator("[data-testid='assistant-tools']").InnerTextAsync())
            .ShouldContain("validate_descriptor");

        await session.Page.ClickAsync("[data-testid='assistant-review']");
        await session.Page.WaitForURLAsync("**/schema/preview");

        await session.Page.ClickAsync("button:has-text('Plan this change')");
        await session.Page.GetByText("against the database").First.WaitForAsync();

        /* The reason box renders with the plan, not before it — so this is asserted here rather than on
           arrival. It is pre-filled from what the operator asked and every character is editable; keeping
           it is the ordinary case. */
        (await session.Page.Locator("#apply-reason").InputValueAsync())
            .ShouldBe("assistant: add an invoices entity");

        await session.Page.ClickAsync("button:has-text('Apply these changes')");
        await session.Page.GetByText("Applied as revision").First.WaitForAsync();

        await session.GoAsync("/history");
        var history = await session.Page.Locator("main.a-content").InnerTextAsync();
        history.ShouldContain("assistant: add an invoices entity");
        history.ShouldContain(AdminWorld.AdminEmail);

        session.AssertConsoleClean();
    }
}

/// <summary>
/// A refused proposal, and the control it deliberately does not offer.
/// </summary>
/// <param name="world">The running host and browser, with a scripted assistant in it.</param>
public sealed class RefusedProposalScenarios(AssistantWorld world) : IClassFixture<AssistantWorld>
{
    /// <summary>
    /// A refused draft shows the framework's own words and offers no way to apply it.
    /// </summary>
    /// <remarks>
    /// Both halves. The wording is the framework's, because a reworded refusal is the one an operator reads
    /// and nobody tested; and the absence of the review button is what stops "it explained the problem and
    /// then let me do it anyway".
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_proposal_is_reported_verbatim_and_cannot_be_reviewed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        await session.Page.ClickAsync("[data-testid='assistant-launch']");
        await session.Page.FillAsync("#assistant-message", "drop the region code column");
        await session.Page.ClickAsync("[data-testid='assistant-send']");

        await session.Page.Locator("[data-testid='assistant-refusals']").WaitForAsync();

        (await session.Page.Locator("[data-testid='assistant-refusals']").InnerTextAsync())
            .ShouldContain(ScriptedAssistant.RefusalText);
        (await session.Page.Locator("[data-testid='assistant-review']").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }

    /// <summary>The drawer fits a phone, and the launcher does not sit on the bottom bar.</summary>
    /// <remarks>
    /// The editor is the screen an operator is most likely to reach for away from a desk, and a floating
    /// button over a five-item bar at 375 px covers one of the five.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_drawer_fits_a_phone_and_clears_the_bottom_bar()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/schema");

        var launcher = await session.Page.Locator("[data-testid='assistant-launch']").BoundingBoxAsync();
        var bar = await session.Page.Locator("nav.a-bottomnav").BoundingBoxAsync();

        launcher.ShouldNotBeNull();
        bar.ShouldNotBeNull();
        (launcher!.Y + launcher.Height).ShouldBeLessThanOrEqualTo(bar!.Y + 1);

        await session.Page.ClickAsync("[data-testid='assistant-launch']");
        await session.Page.Locator("[data-testid='assistant-drawer']").WaitForAsync();
        await session.AssertNoHorizontalScrollAsync();

        session.AssertConsoleClean();
    }
}

/// <summary>
/// The default world — no AI connection, and therefore no assistant anywhere.
/// </summary>
/// <param name="world">The running host and browser, exactly as the container runs it.</param>
public sealed class NoAssistantScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly string[] _routes = ["", "/schema", "/data", "/rules", "/access", "/settings"];

    /// <summary>
    /// With no connection configured there is no launcher on any screen, at either width.
    /// </summary>
    /// <remarks>
    /// Spec §3.3: no launcher, no badge, no explanation to read. Measured on every top-level route rather
    /// than on the one somebody remembers, because the screen that renders it is always the one nobody
    /// opened.
    /// </remarks>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1280)]
    [InlineData(375)]
    public async Task A_deployment_with_no_connection_shows_no_assistant(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);

        foreach (var route in _routes)
        {
            await session.GoAsync(route);
            (await session.Page.Locator("[data-testid='assistant-launch']").CountAsync()).ShouldBe(0);
        }

        session.AssertConsoleClean();
    }
}
