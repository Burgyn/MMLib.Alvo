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
    /// Ruling Q-B: a refused row never reaches the condition. What a number box was given is not syntax, and the hook saved
    /// keeps the last condition the rows could write — never the refused text.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_row_never_reaches_the_saved_condition()
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
        await session.Page.FillAsync("#hook-reject", "Out of stock.");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        var saved = await session.Page.Locator("#hook-beforeCreate-0").InnerTextAsync();
        saved.ShouldContain("new.stock_quantity == 0");
        saved.ShouldNotContain("||");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_guided_rows_fit_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "customers");
        await OpenNewAsync(session);
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "first_name");
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
