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
        await session.Page.GetByText("1 line.").WaitForAsync();
        await session.Page.Keyboard.PressAsync("Enter");

        /* The line count is drawn from the circuit's copy of the text, so two lines is the Enter handled there. */
        await session.Page.GetByText("2 lines.").WaitForAsync();
        (await session.Page.InputValueAsync("#import-json")).ShouldContain("\n");
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "Enter alone does not import");
        session.Page.Url.ShouldEndWith("/transfer");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await session.Page.Locator("#import-json").FocusAsync();
            await session.Page.Keyboard.PressAsync("Control+Enter");
            await EditorScenarios.WaitForFocusInsideAsync(session, "error-panel");
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
