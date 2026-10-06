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
        await session.GoToImportAsync();
        await session.Page.Locator("#import-json").FocusAsync();

        await session.Page.Keyboard.TypeAsync("{ \"not\": ");
        await session.Page.GetByText("1 line,").WaitForAsync();
        await session.Page.Keyboard.PressAsync("Enter");

        /* The line count is drawn from the measure the box tells the circuit, so two lines is the Enter heard there. */
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
    /// <remarks>
    /// Nothing is waited for between the fill and the key except the box's own value, which is read in the browser and
    /// asks nothing of the circuit; the page having loaded the copy is waited for before the fill, because until then the
    /// import is refused by design (<c>ImportGate</c>), and that refusal is not what this scenario is about.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_pasted_import_goes_to_its_plan_on_Meta_Enter()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var descriptor = JsonNode.Parse(Descriptors.FieldService)!.AsObject();
        descriptor["description"] = "Imported through the box.";

        await session.ImportByChordAsync(descriptor.ToJsonString());

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
        await session.GoToImportAsync();
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
        await session.GoToImportAsync();
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
/// A realistic descriptor imports whole, streamed rather than sent in a circuit message, and a paste over the box's ceiling
/// is refused in place, naming the limit — at the box, and by the server for a client that went around the box — never by a
/// circuit that closes without a word (#316, Ruling U-B).
/// </summary>
/// <remarks>Its own world: the whole-example import replaces the operator's working copy.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ImportSizeScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    /// <summary>The whole bike-workshop example, prose included: over SignalR's default 32 KB, which the hub keeps.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_whole_bike_workshop_example_imports_through_the_box()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var descriptor = JsonNode.Parse(Descriptors.BikeWorkshop)!.AsObject();
        descriptor["description"] = "The whole example, through the box.";
        var text = descriptor.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        text.Length.ShouldBeGreaterThan(32 * 1024, "the scenario is the size that used to close the circuit");

        await session.ImportByChordAsync(text);

        await session.WaitForPlanAsync();
        (await session.Content.InnerTextAsync()).ShouldContain("The whole example, through the box.");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Over the character ceiling: refused at the box, the box given back its last text within the ceiling, the circuit
    /// still there to hear the next keystroke, and the refusal gone with the next edit. A paste of over 2 MiB in UTF-8 and
    /// under the character ceiling, which the old circuit-message limit refused, is now let through: the stream carries it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_paste_over_the_limit_is_refused_in_place_and_the_circuit_stays()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoToImportAsync();
        await session.Page.Locator("#import-json").FocusAsync();
        await session.Page.Keyboard.TypeAsync("{");
        await session.Page.GetByText("1 line,").WaitForAsync();

        await session.Page.FillAsync("#import-json", "{\"description\": \"" + new string('x', 1_100_000) + "\"}");
        var refusal = session.Page.GetByTestId("error-panel");
        await refusal.GetByText("1,100,019 characters", new() { Exact = false }).WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("up to 1,000,000 characters");
        (await session.Page.InputValueAsync("#import-json")).ShouldBe("{", "the box keeps its last text within the ceiling");

        await session.Page.FillAsync("#import-json", new string('漢', 700_000) + "\n");
        await session.Page.GetByText("2 lines,").WaitForAsync();
        await refusal.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.InputValueAsync("#import-json")).Length.ShouldBe(700_001, "2.1 MB as UTF-8, and under the character ceiling");

        await session.Page.FillAsync("#import-json", "{\n\n}");
        await session.Page.GetByText("3 lines,").WaitForAsync();
        session.Page.Url.ShouldEndWith("/transfer");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// A client that goes around the box's own ceiling (here, the attribute alvo.js reads it from removed) is refused by the
    /// server on submit: by characters once the text is read, and by bytes from the stream's declared length, before a byte
    /// is read. Each refusal is in place, the circuit stays, and the next edit clears it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_paste_that_went_around_the_box_is_refused_by_the_server_by_characters_and_by_bytes()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoToImportAsync();
        await session.Page.Locator("#import-json").EvaluateAsync("box => box.removeAttribute('data-alvo-max-chars')");
        var refusal = session.Page.GetByTestId("error-panel");

        await session.Page.FillAsync("#import-json", "{\"description\": \"" + new string('x', 1_100_000) + "\"}");
        await session.Page.GetByTestId("import-run").ClickAsync();
        await refusal.GetByText("1,100,019 characters", new() { Exact = false }).WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("up to 1,000,000 characters");

        await session.Page.FillAsync("#import-json", new string('漢', 1_100_000));
        await refusal.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.GetByTestId("import-run").ClickAsync();
        await refusal.GetByText("That paste is 3,223 KB and was not loaded.", new() { Exact = false }).WaitForAsync();

        await session.Page.FillAsync("#import-json", "{\n}");
        await session.Page.GetByText("2 lines,").WaitForAsync();
        await refusal.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.Page.Url.ShouldEndWith("/transfer");
        session.AssertConsoleClean();
    }
}

/// <summary>A descriptor of hundreds of kilobytes imports whole (#316, review I2).</summary>
/// <remarks>Its own world: the import replaces the working copy, and <see cref="ImportSizeScenarios"/> imports too.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class LargeImportScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    /// <summary>
    /// A descriptor far past SignalR's 32 KB message limit, which the hub keeps, and still under the box's ceiling — about
    /// 680 KB — reaches the circuit whole: the stream carries what the box lets through (review I2, Ruling U-B).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_descriptor_of_hundreds_of_kilobytes_imports_through_the_box()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoToImportAsync();
        var descriptor = JsonNode.Parse(Descriptors.BikeWorkshop)!.AsObject();
        descriptor["description"] = "Large. " + new string('x', 650_000);
        var text = descriptor.ToJsonString();
        text.Length.ShouldBeInRange(500_000, 900_000);

        await session.Page.FillAsync("#import-json", text);
        await session.Page.GetByText("1 line,").WaitForAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "under the ceiling, nothing is refused");
        await session.Page.Locator("#import-json").PressAsync("Meta+Enter");

        await session.WaitForImportedAsync();
        await session.WaitForPlanAsync();
        /* What landed is the paste, not a copy cut short: the diff names the description the paste changed. */
        (await session.Content.InnerTextAsync()).ShouldContain("\"description\": \"Large. xxxxxxxxxxxxxxxx");
        session.AssertConsoleClean();
    }
}
