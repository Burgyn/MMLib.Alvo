using Microsoft.Playwright;

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
            .ShouldContain("propose_change");

        await session.Page.ClickAsync("[data-testid='assistant-review']");
        await session.Page.WaitForURLAsync("**/changes");

        await session.WaitForPlanAsync();
        await session.Page.GetByText("against the database").First.WaitForAsync();

        /* The reason box renders with the plan, not before it — so this is asserted here rather than on
           arrival. It is pre-filled from what the operator asked and every character is editable; keeping
           it is the ordinary case. */
        (await session.Page.Locator("#apply-reason").InputValueAsync())
            .ShouldBe("assistant: add an invoices entity");

        await session.Button("Apply these changes").ClickAsync();
        await session.Page.GetByText("Applied as revision").First.WaitForAsync();

        await session.GoAsync("/history");
        var history = await session.Content.InnerTextAsync();
        history.ShouldContain("assistant: add an invoices entity");
        history.ShouldContain(AdminWorld.AdminEmail);

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A turn's details show the calls it made, and copy as the JSON a maintainer can paste into an issue (spec §8, D46).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_turns_details_show_its_calls_and_copy_as_json()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await session.GoAsync("/schema");

        await session.Page.ClickAsync("[data-testid='assistant-launch']");
        await session.Page.FillAsync("#assistant-message", "add an invoices entity");
        await session.Page.ClickAsync("[data-testid='assistant-send']");
        await session.Page.Locator("[data-testid='assistant-proposal']").WaitForAsync();

        await session.Page.ClickAsync("[data-testid='assistant-turn-details'] > summary");
        (await session.Page.Locator("[data-testid='assistant-trace']").InnerTextAsync()).ShouldContain("\"tool\": \"propose_change\"");
        await session.Page.ClickAsync("[data-testid='assistant-trace-copy']");

        await session.SnackbarAsync("Turn details copied");
        var copied = await session.Page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        System.Text.Json.Nodes.JsonNode.Parse(copied)!["calls"]!.AsArray().Count.ShouldBe(2);
        copied.ShouldNotContain("add an invoices entity");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// The launcher and the open drawer look as they did before the component library was referenced (spec D8).
    /// </summary>
    /// <remarks>
    /// The launcher is the app bar's <c>AlvoButton</c> and the question box a <c>MudTextField</c>, so what this
    /// guards is Alvo's own markup around them: the pane, its thread and its head.
    /// <see cref="FoundationScenarios"/> measures the screens. The assistant has its own world, so it is measured
    /// here.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_library_changes_no_style_of_the_assistant()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        var closed = await LibraryProbe.ReadAsync(session.Page, "button[data-testid=assistant-launch]");
        closed.Missing.ShouldBeEmpty();
        closed.Differences.ShouldBeEmpty("MudBlazor's sheet restyles the assistant's launcher");

        await session.Page.ClickAsync("[data-testid='assistant-launch']");
        await session.Page.Locator("#assistant-message").WaitForAsync();
        var open = await LibraryProbe.ReadAsync(session.Page, "#assistant-message");
        open.Missing.ShouldBeEmpty();
        open.Differences.ShouldBeEmpty("MudBlazor's sheet restyles the open assistant");
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
    /// button over a five-item bar at 375 px covered one of the five. The launcher is in the app bar now, above
    /// the bottom bar by construction; the assertion stays so a launcher that floats again is caught.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_drawer_fits_a_phone_and_clears_the_bottom_bar()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/schema");

        var launcher = await session.Page.Locator("[data-testid='assistant-launch']").BoundingBoxAsync();
        var bar = await session.Page.GetByTestId("bottom-nav").BoundingBoxAsync();

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
/// Configuring the assistant from the dashboard, in the session that configured it.
/// </summary>
/// <param name="world">A host with an agent installed, a writable secret store and no connection saved.</param>
public sealed class ConfiguringTheAssistantScenarios(ConfigurableAssistantWorld world)
    : IClassFixture<ConfigurableAssistantWorld>
{
    /// <summary>
    /// Saving a connection lights the launcher, without a reload.
    /// </summary>
    /// <remarks>
    /// <b>Reported from a phone, and the "without a reload" is the whole scenario.</b> The shell resolved
    /// "is an assistant configured" once per circuit, so an operator who configured one on Settings read a
    /// panel saying <c>configured</c> beside a shell with no launcher in it, for the rest of the session and
    /// with nothing on screen suggesting a reload would fix it. Measured in a browser rather than over the
    /// gateway because the defect was entirely in when the shell asked — the write worked the whole time.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Saving_a_connection_lights_the_launcher_without_a_reload()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        /* The premise: nothing is configured, so nothing is mounted. */
        (await session.Page.Locator("[data-testid='assistant-launch']").CountAsync()).ShouldBe(0);

        await SettingsScenarios.OpenConnectionEditorAsync(session);
        await session.Page.FillAsync("#ai-endpoint", "http://127.0.0.1:1/v1");
        await session.Page.FillAsync("#ai-model", "scripted");
        await session.Page.ClickAsync("[data-testid='ai-save']");

        /* No GoAsync, no reload: the same circuit that saved has to grow the launcher. */
        await session.Page.Locator("[data-testid='assistant-launch']").WaitForAsync();

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

/// <summary>
/// Taking a proposal to Preview over unapplied edits asks first, in the discard's own words, and Cancel keeps both the
/// edits and the proposal; over a clean copy it goes straight to Preview (spec §3.2, final review I4).
/// </summary>
/// <remarks>Its own world: it stages into, and then replaces, the one working copy its operator has.</remarks>
/// <param name="world">The running host and browser, with a scripted assistant in it.</param>
public sealed class ProposalOverEditsScenarios(AssistantWorld world) : IClassFixture<AssistantWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Reviewing_a_proposal_over_unapplied_edits_asks_first_and_over_a_clean_copy_does_not()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await KeptFollowScenarios.StageEntityAsync(session, "vendors");
        await AskForInvoicesAsync(session);

        var confirm = session.Dialog("assistant-replace-confirm");
        await session.Page.GetByTestId("assistant-review").ClickAsync();
        await confirm.GetByText("Discard 1 unapplied change?", new() { Exact = true }).WaitForAsync();
        await confirm.GetByTestId("assistant-replace-cancel").ClickAsync();
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.FocusAfterConfirmAsync("assistant-replace-confirm", "[data-testid='assistant-review']");
        session.Page.Url.ShouldEndWith("/schema/vendors");
        (await session.Page.GetByTestId("pending-count").InnerTextAsync()).ShouldBe("1", "Cancel keeps the staged edit");
        await session.Page.GetByTestId("assistant-proposal").WaitForAsync();

        await session.Page.GetByTestId("assistant-review").ClickAsync();
        await confirm.GetByTestId("assistant-replace-run").ClickAsync();
        await session.Page.WaitForURLAsync("**/changes");
        await session.WaitForPlanAsync();
        await session.FocusAfterConfirmAsync("assistant-replace-confirm", "h1");
        (await session.Content.InnerTextAsync()).ShouldNotContain("vendors");

        await session.Page.GetByTestId("discard").First.ClickAsync();
        await session.Dialog("discard-sheet").GetByTestId("discard-confirm").ClickAsync();
        await session.Page.WaitForURLAsync("**/schema");
        await session.FocusAfterConfirmAsync("discard-sheet@Preview", "h1");
        await session.Page.GetByTestId("assistant-review").ClickAsync();
        await session.Page.WaitForURLAsync("**/changes");
        await session.WaitForPlanAsync();
        (await confirm.CountAsync()).ShouldBe(0, "a clean copy has nothing to lose");
        session.AssertConsoleClean();
    }

    private static async Task AskForInvoicesAsync(AdminSession session)
    {
        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        await session.Page.FillAsync("#assistant-message", "add an invoices entity");
        await session.Page.GetByTestId("assistant-send").ClickAsync();
        await session.Page.GetByTestId("assistant-proposal").WaitForAsync();
    }
}
