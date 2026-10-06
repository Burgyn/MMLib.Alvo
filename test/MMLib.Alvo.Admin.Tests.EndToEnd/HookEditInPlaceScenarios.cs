using Microsoft.Playwright;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A hook is edited where it sits (spec §4.1, §4.2, §5.1; ruling B1): it keeps its place in its point's ordered list, it
/// is lit, the sheet is titled by the hook, and an edit that another tab overtook is refused in place rather than written.
/// </summary>
/// <remarks>
/// One shared working copy for the class, so each scenario edits a different hook of the bike-workshop descriptor:
/// service_orders beforeUpdate (two hooks), order_lines beforeCreate (opened, never saved), rentals afterCreate, and
/// customers beforeDelete (made by the scenario itself).
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class HookEditInPlaceScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Editing_a_hook_keeps_its_place_lights_it_and_says_saved()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "service_orders");
        var editor = await OpenEditAsync(session, "beforeUpdate", 0);

        await session.Page.FillAsync("#hook-reject", "Collected <b>orders</b> stay closed.");
        await editor.GetByTestId("hook-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.SnackbarAsync("Hook beforeUpdate saved to the working copy");
        var row = session.Page.Locator("#hook-beforeUpdate-0");
        (await row.InnerTextAsync()).ShouldContain("Collected <b>orders</b> stay closed.");
        (await row.Locator("b").CountAsync()).ShouldBe(0, "a message is text, never markup");
        (await row.GetAttributeAsync("data-alvo-new")).ShouldBe("true");
        (await row.GetByTestId("hook-staged").CountAsync()).ShouldBe(1, "an edited hook badges as new (ruling B2)");
        (await session.Page.Locator("#hook-beforeUpdate-1").InnerTextAsync()).ShouldContain("completed_at", Case.Sensitive, "the next hook did not move");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_edit_sheet_is_titled_by_the_hook_and_keeps_its_point()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "order_lines");
        var editor = await OpenEditAsync(session, "beforeCreate", 0);

        (await editor.InnerTextAsync()).ShouldContain("Edit beforeCreate hook 1");
        (await editor.GetByTestId("hook-save").InnerTextAsync()).ShouldBe("Save to the working copy");
        (await editor.GetByTestId("hook-point-fixed").InnerTextAsync()).ShouldBe("beforeCreate");
        (await editor.GetByTestId("hook-points").CountAsync()).ShouldBe(0, "an opened hook keeps its point (spec D1)");
        (await session.Page.GetByTestId("hook-condition-readout").InnerTextAsync()).ShouldBe("new.quantity <= 0");

        await session.WaitForFocusInsideAsync("hook-editor");
        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Escape_on_a_changed_hook_asks_before_it_discards()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "order_lines");
        var editor = await OpenEditAsync(session, "beforeCreate", 0);
        var declared = await session.Page.Locator("#hook-beforeCreate-0").InnerTextAsync();

        await session.Page.FillAsync("#hook-reject", "A changed message.");
        await session.Page.Keyboard.PressAsync("Escape");
        await editor.GetByTestId("editor-discard-question").WaitForAsync();
        await editor.GetByTestId("editor-keep").ClickAsync();
        /* Focus is back on the form's first control — the condition's mode switch, since Edit draws no point chips — before
           the next Escape: pressed while Keep editing still had focus, it would only press Keep editing again. The switch's
           chips carry no id, so the wait is for focus inside it. */
        await session.WaitForFocusInsideAsync("hook-condition-mode");

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.GetByTestId("editor-discard-question").WaitForAsync();
        await editor.GetByTestId("editor-discard").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.Locator("#hook-beforeCreate-0").InnerTextAsync()).ShouldBe(declared);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_another_tab_removed_is_not_overwritten()
    {
        await using var editing = await world.SignInAsync(TestContext.Current.CancellationToken);
        await using var other = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(editing, "rentals");
        var editor = await OpenEditAsync(editing, "afterCreate", 0);
        await editing.TypeConditionAsync("new.status == 'active'");

        await OnWriteAsync(other, "rentals");
        await other.Page.Locator("#hook-afterCreate-0 [data-testid='hook-remove']").ClickAsync();
        await other.Dialog("remove-hook").GetByTestId("remove-hook-run").ClickAsync();
        /* The removal reaches the editing tab's circuit through the shared working copy, on its own time: Save pressed
           before it arrives would race it. The row going from the list behind the sheet is the sign it arrived. */
        await editing.Page.Locator("#hook-afterCreate-0").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await editor.GetByTestId("hook-save").ClickAsync();
        var refusal = editor.GetByTestId("error-panel");
        await refusal.WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("changed in the working copy after you opened it");
        await editing.WaitForFocusInsideAsync("error-panel");
        (await editor.IsVisibleAsync()).ShouldBeTrue("the sheet stays open with what was typed");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_moved_by_another_tab_is_still_the_one_saved()
    {
        await using var editing = await world.SignInAsync(TestContext.Current.CancellationToken);
        await using var other = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(editing, "customers");
        await AddRejectAsync(editing, "beforeDelete", "First.");
        await AddRejectAsync(editing, "beforeDelete", "Second.");
        var editor = await OpenEditAsync(editing, "beforeDelete", 1);
        await editing.Page.FillAsync("#hook-reject", "Second, edited.");

        await OnWriteAsync(other, "customers");
        await other.Page.Locator("#hook-beforeDelete-0 [data-testid='hook-remove']").ClickAsync();
        await other.Dialog("remove-hook").GetByTestId("remove-hook-run").ClickAsync();
        /* As above: the list behind the sheet loses its second row once the removal has reached this circuit. */
        await editing.Page.Locator("#hook-beforeDelete-1").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await editor.GetByTestId("hook-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        var row = editing.Page.Locator("#hook-beforeDelete-0");
        (await row.InnerTextAsync()).ShouldContain("Second, edited.");
        (await row.GetAttributeAsync("data-alvo-new")).ShouldBe("true");
        (await editing.Page.Locator("#hook-beforeDelete-1").CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_condition_over_two_thousand_characters_is_said_under_the_box()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "bikes");
        await session.Page.GetByTestId("hook-new").ClickAsync();

        await session.TypeConditionAsync("new.brand == '" + new string('x', 1990) + "'");
        var sentence = session.Page.GetByTestId("hook-condition-length");
        await sentence.WaitForAsync();
        (await sentence.InnerTextAsync()).ShouldContain("2000");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_Add_adds_one_hook()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "parts");
        var before = await session.Page.GetByTestId("hook-row").CountAsync();
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.FillAsync("#hook-reject", "Parts are added by purchasing.");

        await session.Dialog("hook-editor").GetByTestId("hook-add").DblClickAsync();

        await session.Page.GetByTestId("hook-row").Nth(before).WaitForAsync();
        await session.SnackbarAsync("added to the working copy");
        var editor = session.Dialog("hook-editor");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        /* One more round trip before counting. A circuit handles events in order, so once New hook has opened the sheet
           again, a second Add queued behind the first has run too — and would show here as a second row. */
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await editor.WaitForAsync();
        await session.WaitForFocusInsideAsync("hook-editor");
        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.GetByTestId("hook-row").CountAsync()).ShouldBe(before + 1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_edit_sheet_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await OnWriteAsync(session, "order_lines");
        await OpenEditAsync(session, "beforeCreate", 0);

        await session.AssertNoHorizontalScrollAsync();
    }

    /// <summary>The entity tab that lists its hooks.</summary>
    internal const string OnWriteTab = "On write";

    internal static async Task OnWriteAsync(AdminSession session, string entity)
    {
        await session.GoAsync($"/schema/{entity}");
        await session.OpenTabAsync(OnWriteTab);
    }

    internal static async Task<ILocator> OpenEditAsync(AdminSession session, string point, int position)
    {
        await session.Page.Locator($"#hook-{point}-{position} [data-testid='hook-edit']").ClickAsync();
        var editor = session.Dialog("hook-editor");
        await editor.WaitForAsync();
        return editor;
    }

    private static async Task AddRejectAsync(AdminSession session, string point, string message)
    {
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = point, Exact = true }).ClickAsync();
        await session.Page.FillAsync("#hook-reject", message);
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }
}

/// <summary>
/// A hook the editor cannot draw is read-only, and an edit beside it keeps it (spec §5.2) — its own world, because the
/// descriptor carrying it arrives by import, which replaces the working copy every other scenario of a world shares.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class HookShapeScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_the_editor_cannot_draw_is_read_only_and_survives_an_edit_beside_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var descriptor = JsonNode.Parse(Descriptors.BikeWorkshop)!.AsObject();
        /* Not an empty payload: the schema gives entity.update's payload minProperties: 1, so {} fails the import. */
        descriptor["entities"]!["rentals"]!["hooks"]!["afterUpdate"] = JsonNode.Parse(
            """[ { "action": { "type": "entity.update", "entity": "rental_fleet", "payload": { "in_service": false } } } ]""");
        await session.ImportByChordAsync(descriptor.ToJsonString());

        await HookEditInPlaceScenarios.OnWriteAsync(session, "rentals");
        var readOnly = session.Page.GetByTestId("hook-readonly");
        await readOnly.WaitForAsync();
        (await readOnly.InnerTextAsync()).ShouldContain("'entity.update' is refused");
        (await session.Page.Locator("#hook-afterUpdate-0 [data-testid='hook-edit']").CountAsync()).ShouldBe(0);

        var editor = await HookEditInPlaceScenarios.OpenEditAsync(session, "afterCreate", 0);
        await session.TypeConditionAsync("new.status == 'active'");
        await editor.GetByTestId("hook-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#hook-afterUpdate-0").InnerTextAsync()).ShouldContain("entity.update");
        await readOnly.WaitForAsync();
    }
}
