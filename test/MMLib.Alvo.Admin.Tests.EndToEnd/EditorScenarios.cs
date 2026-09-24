using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The side-sheet editor behaves the same way everywhere (spec §3.1, §3.4): focus lands on the first field, Escape
/// closes and gives focus back, unsaved work is guarded, a submit cannot run twice, and a refusal takes focus.
/// </summary>
/// <remarks>
/// <para>Its own world: it stages fields, and the working copy is one per operator.</para>
/// <para>
/// Focus is waited for, never read once: the dialog's focus trap moves it over the circuit, after the render that
/// drew the dialog, so a reading taken straight after the click lands before it.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class EditorScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly PageWaitForFunctionOptions _polling = new() { PollingInterval = 100 };

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_editor_opens_on_its_first_field_and_Escape_gives_focus_back_to_the_trigger()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        var trigger = session.Page.GetByTestId("add-field");

        await trigger.ClickAsync();
        var editor = session.Dialog("field-sheet");
        await editor.WaitForAsync();

        (await editor.GetAttributeAsync("aria-modal")).ShouldBe("true");
        await session.Page.WaitForFunctionAsync("() => document.activeElement?.id === 'new-field-name'", null, _polling);
        (await session.FocusedAsync()).ShouldStartWith("input#new-field-name");

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.WaitForFunctionAsync(
            "() => document.activeElement?.dataset.testid === 'add-field'", null, _polling);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_editor_with_changes_asks_before_it_closes_and_keeps_them_when_told_to()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var editor = session.Dialog("field-sheet");
        await session.Page.FillAsync("#new-field-name", "half_typed");

        await session.Page.Keyboard.PressAsync("Escape");
        var question = editor.GetByTestId("editor-discard-question");
        await question.WaitForAsync();
        await session.Page.WaitForFunctionAsync(
            "() => !!document.activeElement?.closest(\"[data-testid='editor-discard-question']\")", null, _polling);

        await editor.GetByTestId("editor-keep").ClickAsync();
        (await session.Page.InputValueAsync("#new-field-name")).ShouldBe("half_typed");

        await editor.GetByTestId("editor-cancel").ClickAsync();
        await editor.GetByTestId("editor-discard").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("field-row-half_typed").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_save_stages_one_field_and_says_so()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.FillAsync("#new-field-name", "loyalty_tier");

        await session.Dialog("field-sheet").GetByTestId("field-save").DblClickAsync();

        await session.SnackbarAsync("Saved to the working copy");
        await session.Page.GetByTestId("field-row-loyalty_tier").WaitForAsync();
        await OutwaitASecondSubmitAsync(session);
        (await session.Page.GetByTestId("field-row-loyalty_tier").CountAsync()).ShouldBe(1);
        (await session.Page.GetByTestId("staged-loyalty_tier").CountAsync()).ShouldBe(1);
        (await session.SnackbarCountAsync("Saved to the working copy")).ShouldBe(1, "one save, said once");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Ctrl+Enter in a single-line field is one submit: the browser's own Enter-submit is cancelled, and alvo.js's
    /// <c>requestSubmit</c> is the only path (spec §3.4).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Ctrl_Enter_in_a_single_line_field_saves_once()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var editor = session.Dialog("field-sheet");
        await session.Page.FillAsync("#new-field-name", "chord_single");

        await session.Page.Locator("#new-field-name").PressAsync("Control+Enter");

        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.SnackbarAsync("Saved to the working copy");
        await OutwaitASecondSubmitAsync(session);
        (await session.Page.GetByTestId("staged-chord_single").CountAsync()).ShouldBe(1);
        (await session.SnackbarCountAsync("Saved to the working copy")).ShouldBe(1, "one chord, one submit");
        session.AssertConsoleClean();
    }

    /// <summary>In a textarea Enter is a newline, and Ctrl+Enter is one submit (spec §3.4).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task In_a_textarea_Enter_is_a_newline_and_Ctrl_Enter_saves_once()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var editor = session.Dialog("field-sheet");
        await session.Page.FillAsync("#new-field-name", "chord_multi");
        await editor.GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        await editor.GetByRole(AriaRole.Radio, new() { Name = "integer", Exact = true }).ClickAsync();
        var expression = session.Page.Locator("#new-field-computed");
        await expression.FillAsync("priority");

        await expression.PressAsync("Enter");
        await session.Page.Keyboard.TypeAsync("+ priority");
        await OutwaitASecondSubmitAsync(session);
        (await expression.InputValueAsync()).ShouldBe("priority\n+ priority");
        (await session.SnackbarCountAsync()).ShouldBe(0, "Enter in a textarea is a newline, not a submit");

        await expression.PressAsync("Control+Enter");

        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.SnackbarAsync("Saved to the working copy");
        await OutwaitASecondSubmitAsync(session);
        (await session.Page.GetByTestId("staged-chord_multi").CountAsync()).ShouldBe(1);
        (await session.SnackbarCountAsync("Saved to the working copy")).ShouldBe(1, "one chord, one submit");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_field_is_an_alert_in_the_editor_and_it_takes_focus()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.FillAsync("#new-field-name", "Not A Name");

        await session.Dialog("field-sheet").GetByTestId("field-save").ClickAsync();

        var alert = session.Dialog("field-sheet").GetByTestId("error-panel");
        await alert.WaitForAsync();
        (await alert.GetAttributeAsync("role")).ShouldBe("alert");
        await session.Page.WaitForFunctionAsync(
            "() => !!document.activeElement?.closest(\"[data-testid='error-panel']\")", null, _polling);
        (await session.SnackbarCountAsync()).ShouldBe(0, "an error is never a snackbar");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_field_cannot_happen_without_its_confirm()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        var remove = session.Page.GetByTestId("remove-field-name");

        await remove.ClickAsync();
        var confirm = session.Dialog("remove-field-sheet");
        await confirm.WaitForAsync();
        (await confirm.GetAttributeAsync("aria-modal")).ShouldBe("true");
        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("staged-name").CountAsync()).ShouldBe(0, "Escape is Cancel");
        session.AssertConsoleClean();
    }

    /// <summary>A field nothing names asks too, and only the verb removes it (spec §3.2).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_field_nothing_names_is_removed_by_the_verb_and_kept_by_Cancel()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        var confirm = session.Dialog("remove-field-sheet");

        await session.Page.GetByTestId("remove-field-phone").ClickAsync();
        await confirm.GetByTestId("remove-field-cancel").ClickAsync();
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("staged-phone").CountAsync()).ShouldBe(0, "Cancel keeps the field");

        await session.Page.GetByTestId("remove-field-phone").ClickAsync();
        await confirm.GetByTestId("remove-field-anyway").ClickAsync();
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.GetByTestId("staged-phone").WaitForAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Discarding_the_working_copy_is_a_confirm_whose_Cancel_keeps_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.FillAsync("#new-field-name", "kept_until_discarded");
        await session.Dialog("field-sheet").GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId("pending-bar").WaitForAsync();

        await session.Page.GetByTestId("pending-discard").ClickAsync();
        await session.Dialog("discard-sheet").GetByTestId("discard-cancel").ClickAsync();
        await session.Dialog("discard-sheet").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("pending-bar").IsVisibleAsync()).ShouldBeTrue();

        await session.Page.GetByTestId("pending-discard").ClickAsync();
        await session.Dialog("discard-sheet").GetByTestId("discard-confirm").ClickAsync();
        await session.SnackbarAsync("Discarded the working copy");
        await session.Page.GetByTestId("pending-bar").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Gives a second submit, if one was sent, the time to arrive and draw: a count read at once would pass before it
    /// landed.
    /// </summary>
    private static Task OutwaitASecondSubmitAsync(AdminSession session) => session.Page.WaitForTimeoutAsync(500);
}
