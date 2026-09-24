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
        await session.Page.Keyboard.PressAsync("Enter");
        (await session.Page.InputValueAsync("#import-json")).ShouldContain("\n");
        await ServerHoldsTheTextAsync(session);
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "Enter alone does not import");

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

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_good_import_goes_to_preview_on_Meta_Enter()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");

        await session.Page.FillAsync("#import-json", Descriptors.ComplexCrm);
        await ServerHoldsTheTextAsync(session);
        await session.Page.Locator("#import-json").PressAsync("Meta+Enter");

        /* The preview of the imported document, not its plan: complex-crm declares CEL defaults this build's
           planner refuses, which the preview then says in place. What this measures is that the key imported. */
        await session.Page.WaitForURLAsync("**/changes");
        await session.Content.GetByText("\"name\": \"crm\"").First.WaitForAsync();
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Waits until the circuit holds what was typed, which the Import button's enabling says. The key is its own
    /// event, and the library's text update is asynchronous, so a key pressed within the same instant as the fill can
    /// be handled before the text it should submit.
    /// </summary>
    private static async Task ServerHoldsTheTextAsync(AdminSession session)
        => await session.Page.WaitForFunctionAsync(
            "() => document.querySelector(\"[data-testid='import-run']\")?.disabled === false");
}
