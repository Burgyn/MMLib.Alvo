using Microsoft.Playwright;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The import box submits on Ctrl/Cmd+Enter, and a refusal is an alert that takes focus (spec §3.3, §3.4).</summary>
/// <remarks>
/// Its own world: the good import replaces the operator's working copy, which every other scenario of a world shares.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class TransferScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_import_is_an_alert_with_focus_and_Enter_alone_is_a_newline()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");
        await session.Page.Locator("#import-json").FocusAsync();

        await session.Page.Keyboard.TypeAsync("{ \"not\": ");
        await session.Page.GetByText("1 line,").WaitForAsync();
        await session.Page.Keyboard.PressAsync("Enter");

        /* The line count is drawn from the circuit's copy of the text, so two lines is the Enter handled there. */
        await session.Page.GetByText("2 lines,").WaitForAsync();
        (await session.Page.InputValueAsync("#import-json")).ShouldContain("\n");
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "Enter alone does not import");
        session.Page.Url.ShouldEndWith("/transfer");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await session.Page.Locator("#import-json").FocusAsync();
            await session.Page.Keyboard.PressAsync("Control+Enter");
            await session.WaitForFocusInsideAsync("error-panel");
        }

        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(1);
        (await session.SnackbarCountAsync()).ShouldBe(0, "no error is ever a snackbar");
        session.Page.Url.ShouldEndWith("/transfer");
    }

    /// <summary>
    /// Pasted and submitted in one breath, with nothing waited for in between: the key must import what was pasted,
    /// not what the circuit had heard of so far. field-service with a new description, so the planner can plan it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_pasted_import_goes_to_its_plan_on_Meta_Enter()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");
        var descriptor = JsonNode.Parse(Descriptors.FieldService)!.AsObject();
        descriptor["description"] = "Imported through the box.";

        await session.Page.FillAsync("#import-json", descriptor.ToJsonString());
        await session.Page.Locator("#import-json").PressAsync("Meta+Enter");

        await session.Page.WaitForURLAsync("**/changes");
        await session.WaitForPlanAsync();
        (await session.Content.InnerTextAsync()).ShouldContain("Imported through the box.");
        session.AssertConsoleClean();
    }
}

/// <summary>
/// An import over unapplied edits asks first, in the discard's own words, and Cancel keeps everything; over a clean copy
/// it goes straight to its plan (spec §3.2, final review I4).
/// </summary>
/// <remarks>Its own world: it stages into, and then replaces, the one working copy its operator has.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ImportOverEditsScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_import_over_unapplied_edits_asks_first_and_over_a_clean_copy_does_not()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await KeptFollowScenarios.StageEntityAsync(session, "vendors");
        await session.GoAsync("/transfer");
        await PasteAsync(session, "Imported over the edits.");

        var confirm = session.Dialog("import-replace-confirm");
        await session.Page.GetByTestId("import-run").ClickAsync();
        await confirm.GetByText("Discard 1 unapplied change?", new() { Exact = true }).WaitForAsync();
        await confirm.GetByTestId("import-replace-cancel").ClickAsync();
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.FocusAfterConfirmAsync("import-replace-confirm", "[data-testid='import-run']");
        session.Page.Url.ShouldEndWith("/transfer");
        (await session.Page.GetByTestId("pending-count").InnerTextAsync()).ShouldBe("1", "Cancel keeps the staged edit");

        await session.Page.GetByTestId("import-run").ClickAsync();
        await confirm.GetByTestId("import-replace-run").ClickAsync();
        await session.Page.WaitForURLAsync("**/changes");
        await session.WaitForPlanAsync();
        await session.FocusAfterConfirmAsync("import-replace-confirm", "h1");
        var plan = await session.Content.InnerTextAsync();
        plan.ShouldContain("Imported over the edits.");
        plan.ShouldNotContain("vendors");

        await session.Page.GetByTestId("discard").First.ClickAsync();
        await session.Dialog("discard-sheet").GetByTestId("discard-cancel").ClickAsync();
        await session.FocusAfterConfirmAsync("discard-sheet@Preview", "[data-testid='discard']");
        await session.Page.GetByTestId("discard").First.ClickAsync();
        await session.Dialog("discard-sheet").GetByTestId("discard-confirm").ClickAsync();
        await session.Page.WaitForURLAsync("**/schema");
        await session.GoAsync("/transfer");
        await PasteAsync(session, "Imported over a clean copy.");
        await session.Page.GetByTestId("import-run").ClickAsync();
        await session.Page.WaitForURLAsync("**/changes");
        (await confirm.CountAsync()).ShouldBe(0, "a clean copy has nothing to lose");
        session.AssertConsoleClean();
    }

    private static Task PasteAsync(AdminSession session, string description)
    {
        var descriptor = JsonNode.Parse(Descriptors.FieldService)!.AsObject();
        descriptor["description"] = description;
        return session.Page.FillAsync("#import-json", descriptor.ToJsonString());
    }
}

/// <summary>
/// A realistic descriptor imports whole, and a paste over what one circuit message may carry is refused in place, naming
/// the limit — never a circuit that closes without a word (#316).
/// </summary>
/// <remarks>Its own world: the whole-example import replaces the operator's working copy.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ImportSizeScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    /// <summary>The whole bike-workshop example, prose included: over SignalR's default 32 KB once it is sent.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_whole_bike_workshop_example_imports_through_the_box()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");
        var descriptor = JsonNode.Parse(Descriptors.BikeWorkshop)!.AsObject();
        descriptor["description"] = "The whole example, through the box.";
        var text = descriptor.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        text.Length.ShouldBeGreaterThan(32 * 1024, "the scenario is the size that used to close the circuit");

        await session.Page.FillAsync("#import-json", text);
        await session.Page.Locator("#import-json").PressAsync("Meta+Enter");

        await session.Page.WaitForURLAsync("**/changes");
        await session.WaitForPlanAsync();
        (await session.Content.InnerTextAsync()).ShouldContain("The whole example, through the box.");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Over the character ceiling, and under it but over the bytes it takes as sent: each refused at the box, the box
    /// given back what the circuit last heard, and the circuit still there to hear the next keystroke.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_paste_over_the_limit_is_refused_in_place_and_the_circuit_stays()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");
        await session.Page.Locator("#import-json").FocusAsync();
        await session.Page.Keyboard.TypeAsync("{");
        await session.Page.GetByText("1 line,").WaitForAsync();

        await session.Page.FillAsync("#import-json", "{\"description\": \"" + new string('x', 1_100_000) + "\"}");
        var refusal = session.Page.GetByTestId("error-panel");
        await refusal.GetByText("1,100,019 characters", new() { Exact = false }).WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("up to 1,000,000 characters");
        (await session.Page.InputValueAsync("#import-json")).ShouldBe("{", "the box holds what the circuit last heard");

        await session.Page.FillAsync("#import-json", new string('漢', 700_000));
        await refusal.GetByText("700,000 characters", new() { Exact = false }).WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("1,984 KB");

        await session.Page.FillAsync("#import-json", "{\n}");
        await session.Page.GetByText("2 lines,").WaitForAsync();
        session.Page.Url.ShouldEndWith("/transfer");
        session.AssertConsoleClean();
    }
}
