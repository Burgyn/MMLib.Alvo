using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The keys behave one way on every screen (spec §3.4; final review M5, M6): every multi-line box that submits says
/// what Ctrl/Cmd+Enter does in the same words, and Enter submits every single-line form, Preview's Why included.
/// </summary>
/// <remarks>Its own world: it applies.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class KeyboardConsistencyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private const string Chord = "Ctrl+Enter or ⌘+Enter";

    /// <summary>
    /// Every multi-line box that submits is described by the same sentence (the assistant's was the model; its own
    /// scenarios read it): the import, a rule, a computed field's expression and a record's text.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Every_multi_line_box_names_its_chord_the_same_way()
    {
        await RecordEditorScenarios.SeedWorkOrderAsync(world, TenantId.New(), "WO-0801");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);

        await session.GoAsync("/transfer");
        (await DescriptionAsync(session, "import-json")).ShouldContain($"{Chord} loads it. Enter is a new line.");

        await session.GoAsync("/schema/work_orders?tab=rules");
        (await DescriptionAsync(session, "rule-delete"))
            .ShouldContain($"{Chord} saves the rule. Enter is a new line. Escape puts back the saved rule.");

        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Dialog("field-sheet").GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        (await DescriptionAsync(session, "new-field-computed")).ShouldContain($"{Chord} saves. Enter is a new line.");
        await session.Page.Keyboard.PressAsync("Escape");
        await session.Dialog("field-sheet").GetByTestId("editor-discard").ClickAsync();
        await session.Dialog("field-sheet").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.GoAsync("/data/work_orders");
        await session.Page.GetByTestId("record-new").ClickAsync();
        (await DescriptionAsync(session, "rf-description")).ShouldContain($"{Chord} saves. Enter is a new line.");
        session.AssertConsoleClean();
    }

    /// <summary>Enter in Why applies exactly as the primary action does (spec §3.4).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Enter_in_Why_applies_as_the_primary_does()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "enter_applies");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForURLAsync("**/schema/enter_applies");
        await session.PreviewPendingAsync();

        await session.Page.FillAsync("#apply-reason", "Enter applies");
        await session.Page.Locator("#apply-reason").PressAsync("Enter");

        await session.SnackbarAsync("Applied as revision");
        session.AssertConsoleClean();
    }

    /// <summary>The words every multi-line box is described by: its <c>aria-describedby</c>'s text.</summary>
    private static async Task<string> DescriptionAsync(AdminSession session, string id)
    {
        await session.Page.Locator($"#{id}").WaitForAsync();
        return await session.Page.EvaluateAsync<string>(
            "id => (document.getElementById(id)?.getAttribute('aria-describedby') ?? '').split(' ')"
            + ".map(part => document.getElementById(part)?.textContent ?? '').join(' ').replace(/\\s+/g, ' ')", id);
    }
}

/// <summary>
/// Enter in the typed-name box confirms once the name matches, as the verb would, and does nothing before (spec §3.4;
/// final review M6).
/// </summary>
/// <remarks>Its own world: it applies a drop.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class TypedNameEnterScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Enter_in_the_typed_name_confirms_only_once_the_name_matches()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("remove-field-code").ClickAsync();
        await session.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();
        await session.PreviewPendingAsync();
        await session.Button("Apply these changes").ClickAsync();

        var confirm = session.Dialog("apply-confirm");
        await session.WaitForFocusOnAsync("confirm-name");
        await session.Page.Keyboard.TypeAsync("field-servic");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForTimeoutAsync(300);
        (await confirm.CountAsync()).ShouldBe(1, "almost the name confirms nothing");

        await session.Page.Keyboard.TypeAsync("e");
        await session.Page.WaitForFunctionAsync(
            "() => !document.querySelector(\"[data-testid='apply-confirm-run']\")?.disabled");
        await session.Page.Keyboard.PressAsync("Enter");

        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.SnackbarAsync("Applied as revision");
        session.AssertConsoleClean();
    }
}
