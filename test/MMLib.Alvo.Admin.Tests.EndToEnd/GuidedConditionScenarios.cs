using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The guided condition: rows that write ordinary CEL, a readout the build checks, and a switch to text for anything the
/// rows cannot say (spec §4.6, §7; ruling B7). Each scenario uses a different entity or point: the class shares one copy.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class GuidedConditionScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Rows_write_the_condition_and_the_hook_is_added_with_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "service_orders");
        await OpenNewAsync(session);
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = "beforeUpdate", Exact = true }).ClickAsync();

        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "status");
        await session.ChooseAsync(Combobox(session, "Condition 1 value"), "ready");
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 2 field"), "The person writing");
        await session.ChooseAsync(Combobox(session, "Condition 2 role"), "manager");

        var readout = session.Page.GetByTestId("hook-condition-readout");
        await ReadoutAsync(session, "new.status == 'ready' && 'manager' in @user.roles");
        (await readout.InnerTextAsync()).ShouldBe("new.status == 'ready' && 'manager' in @user.roles");
        await session.Page.FillAsync("#hook-reject", "Only a manager marks an order ready.");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#hook-beforeUpdate-2").InnerTextAsync()).ShouldContain("new.status == 'ready' && 'manager' in @user.roles");
        await session.GoAsync("/changes");
        await session.WaitForPlanAsync();
        await session.Page.Locator("#apply-reason").WaitForAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_canonical_condition_opens_as_rows_and_a_hand_written_one_as_text()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "order_lines");
        await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeCreate", 0);

        (await Mode(session, "Guided").CountAsync()).ShouldBe(1);
        (await session.Page.GetByTestId("hook-condition-readout").InnerTextAsync()).ShouldBe("new.quantity <= 0");
        (await Combobox(session, "Condition 1 operator").InnerTextAsync()).ShouldContain("is at most");
        await session.WaitForFocusInsideAsync("hook-editor", FocusScope.Dialog);
        await session.Page.Keyboard.PressAsync("Escape");
        await session.Dialog("hook-editor").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await HookEditInPlaceScenarios.OnWriteAsync(session, "service_orders");
        await HookEditInPlaceScenarios.OpenEditAsync(session, "afterUpdate", 0);
        (await Mode(session, "Text").CountAsync()).ShouldBe(1, "a bare boolean is not a row (spec D5)");
        (await session.Page.InputValueAsync("input#hook-condition")).ShouldContain("new.notify_customer");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Text_that_cannot_be_rows_stays_text_and_says_why()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "rentals");
        await OpenNewAsync(session);
        await session.TypeConditionAsync("new.status == \"reserved\"");

        await session.Page.GetByTestId("hook-condition-mode").GetByRole(AriaRole.Radio, new() { Name = "Guided", Exact = true }).ClickAsync();

        var note = session.Page.GetByTestId("hook-condition-note");
        await note.WaitForAsync();
        (await note.InnerTextAsync()).ShouldContain("cannot be shown as rows");
        (await Mode(session, "Text").CountAsync()).ShouldBe(1);
        (await session.Page.InputValueAsync("input#hook-condition")).ShouldBe("new.status == \"reserved\"", "the text is kept as typed");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_role_row_is_not_offered_after_the_commit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "bikes");
        await OpenNewAsync(session);
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = "afterCreate", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("condition-add").ClickAsync();

        await Combobox(session, "Condition 1 field").ClickAsync();
        await Option(session, "brand").WaitForAsync();
        (await Option(session, "The person writing").CountAsync()).ShouldBe(0, "an event envelope carries no roles");
        (await Option(session, "brand").CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_negative_number_is_refused_in_the_rows()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "order_lines");
        await OpenNewAsync(session);
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "quantity");

        await session.Page.FillAsync("#condition-0-value", "-5");

        var refusal = session.Page.GetByTestId("condition-refusal");
        await refusal.WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("negative number");
    }

    /// <summary>
    /// Ruling Q-B and S-B: a refused row never reaches the condition, and the sheet will not add the hook while one is on
    /// screen — the refusal is said in the sheet's panel, the readout keeps the last condition the rows could write, and
    /// nothing is staged.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_row_keeps_the_sheet_open_and_stages_nothing()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "parts");
        await OpenNewAsync(session);
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "stock_quantity");
        await session.Page.FillAsync("#condition-0-value", "0");
        await ReadoutAsync(session, "new.stock_quantity == 0");

        await session.Page.FillAsync("#condition-0-value", "0 || true");

        await session.Page.GetByTestId("condition-refusal").WaitForAsync();
        (await session.Page.GetByTestId("hook-condition-readout").InnerTextAsync()).ShouldBe("new.stock_quantity == 0");
        (await session.Page.GetAttributeAsync("#condition-0-value", "aria-describedby") ?? string.Empty)
            .ShouldContain("condition-0-refusal", Case.Sensitive, "the box is described by the refusal under it");
        await session.Page.FillAsync("#hook-reject", "Out of stock.");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();

        var panel = editor.GetByTestId("error-panel");
        await panel.WaitForAsync();
        (await panel.InnerTextAsync()).ShouldContain("not a number");
        (await editor.IsVisibleAsync()).ShouldBeTrue("the sheet stays open with what was typed");
        (await session.Page.Locator("#hook-beforeCreate-0").CountAsync()).ShouldBe(0, "nothing is staged");
    }

    /// <summary>
    /// A row with a field and a relation that takes a value, but no value given, is not a condition the operator wrote:
    /// the sheet refuses to add it rather than adding a hook that runs on every write (review I1).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_unfinished_row_keeps_the_sheet_open_and_says_which()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "technicians");
        await OpenNewAsync(session);
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "hourly_rate");
        await session.Page.FillAsync("#hook-reject", "No.");
        var editor = session.Dialog("hook-editor");

        await editor.GetByTestId("hook-add").ClickAsync();

        var panel = editor.GetByTestId("error-panel");
        await panel.WaitForAsync();
        (await panel.InnerTextAsync()).ShouldContain("Condition 1 needs a value");
        (await session.Page.Locator("#hook-beforeCreate-0").CountAsync()).ShouldBe(0, "nothing is staged");
    }

    /// <summary>Removing a row keeps focus in the sheet, so Escape still closes it (review I2).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_row_keeps_focus_in_the_sheet()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "rental_fleet");
        await OpenNewAsync(session);
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.Page.GetByTestId("condition-add").ClickAsync();
        var remove = session.Page.GetByTestId("condition-1-remove");

        await remove.FocusAsync();
        await session.Page.Keyboard.PressAsync("Enter");
        await remove.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.WaitForFocusOnTestIdAsync("condition-add");

        await session.Page.Keyboard.PressAsync("Escape");
        await session.Dialog("hook-editor").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    /// <summary>
    /// Removing a row that has rows after it hands focus to the Remove of the row that took its place — the same index —
    /// and Escape still closes the sheet (review I2, the path the last-row pin does not take).
    /// </summary>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Removing_a_row_with_rows_after_it_focuses_the_next_row_s_Remove(int index)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "rental_fleet");
        await OpenNewAsync(session);
        for (var added = 0; added < 3; added++)
        {
            await session.Page.GetByTestId("condition-add").ClickAsync();
        }

        await session.Page.GetByTestId("condition-row").Nth(2).WaitForAsync();

        await session.Page.GetByTestId($"condition-{index}-remove").FocusAsync();
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.GetByTestId("condition-2-remove").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.WaitForFocusOnTestIdAsync($"condition-{index}-remove");

        await session.Page.Keyboard.PressAsync("Escape");
        await session.Dialog("hook-editor").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    /// <summary>
    /// A row whose field another tab removed is not quietly dropped from the condition: the sheet will not add the hook
    /// and names the row (ruling S-B). The removal reaches this circuit through the shared working copy.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_row_whose_field_was_removed_elsewhere_keeps_the_sheet_open()
    {
        await using var editing = await world.SignInAsync(TestContext.Current.CancellationToken);
        await using var other = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(editing, "technicians");
        await OpenNewAsync(editing);
        await editing.Page.GetByTestId("condition-add").ClickAsync();
        await editing.ChooseAsync(Combobox(editing, "Condition 1 field"), "phone");
        await editing.Page.FillAsync("#condition-0-value", "112");
        await ReadoutAsync(editing, "new.phone == '112'");
        await editing.Page.FillAsync("#hook-reject", "No.");

        await other.GoAsync("/schema/technicians");
        await other.Page.GetByTestId("remove-field-phone").ClickAsync();
        await other.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();
        await other.Page.GetByTestId("restore-field-phone").WaitForAsync();
        /* The field's going reaches the editing circuit on its own time: the row turns refused in place once it has. */
        await editing.Page.GetByTestId("condition-refusal").WaitForAsync();

        var editor = editing.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();

        var panel = editor.GetByTestId("error-panel");
        await panel.WaitForAsync();
        (await panel.InnerTextAsync()).ShouldContain("Condition 1 names a field that no longer exists.");
        (await editing.Page.Locator("#hook-beforeCreate-0").CountAsync()).ShouldBe(0, "nothing is staged");
    }

    /// <summary>
    /// An empty text compared on purpose — <c>new.color == ''</c> — opens as a row and is saved exactly as it was written:
    /// a value read back counts as given (review I1 keeps '' writable).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_empty_text_condition_is_saved_unchanged_through_the_rows()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "bikes");
        await OpenNewAsync(session);
        await session.TypeConditionAsync("new.color == ''");
        await session.Page.GetByTestId("hook-condition-mode").GetByRole(AriaRole.Radio, new() { Name = "Guided", Exact = true }).ClickAsync();
        await Mode(session, "Guided").WaitForAsync();
        await ReadoutAsync(session, "new.color == ''");
        (await session.Page.InputValueAsync("#condition-0-value")).ShouldBe(string.Empty);

        await session.Page.FillAsync("#hook-reject", "Say the colour.");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#hook-beforeCreate-0").InnerTextAsync()).ShouldContain("new.color == ''");
    }

    /// <summary>The widest row — an update's field, its image chips, a relation, a value and the empty-field hint — fits 375 px.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_guided_rows_fit_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "customers");
        await OpenNewAsync(session);
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = "beforeUpdate", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "first_name");
        await session.Page.GetByTestId("condition-0-image").WaitForAsync();
        await session.Page.FillAsync("#condition-0-value", "Ann");
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.Page.GetByTestId("condition-row").Nth(1).WaitForAsync();

        await session.AssertNoHorizontalScrollAsync();
    }

    /// <summary>Opens the New hook sheet and waits until it holds focus, so no later key or list lands outside it.</summary>
    private static async Task OpenNewAsync(AdminSession session)
    {
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.WaitForFocusInsideAsync("hook-editor", FocusScope.Dialog);
    }

    /// <summary>Waits for the readout to show <paramref name="condition"/>: the rows hand it to the sheet a round trip later.</summary>
    private static Task ReadoutAsync(AdminSession session, string condition)
        => session.Page.GetByTestId("hook-condition-readout").Filter(new() { HasTextString = condition }).WaitForAsync();

    private static ILocator Mode(AdminSession session, string name)
        => session.Page.GetByTestId("hook-condition-mode").GetByRole(AriaRole.Radio, new() { Name = name, Exact = true, Checked = true });

    private static ILocator Combobox(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });

    private static ILocator Option(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Option, new() { Name = name, Exact = true });
}
