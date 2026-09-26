using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// After any confirm, Cancel or its verb, focus is on the trigger, or, when the trigger is gone, on the item's row or
/// the list's create action: never on <c>&lt;body&gt;</c> (spec §3.2; final review M4). These are the confirms with no
/// flow of their own elsewhere; the rest are pinned inside the scenarios that already drive them, through
/// <c>AdminSession.FocusAfterConfirmAsync</c>, and <c>PatternLanguageTests</c> fails any confirm in the source whose
/// Cancel and verb are not both pinned.
/// </summary>
/// <remarks>
/// Its own world: it stages removals, disables a person and deletes a record. Each fact works on its own entity or
/// person, because xUnit v3 runs a class's facts in no set order.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ConfirmFocusScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_an_index_names_it_and_hands_focus_to_the_row_that_took_its_place()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("Indexes");

        var confirm = await AskAsync(session, "index-remove", 0, "remove-index");
        (await confirm.InnerTextAsync()).ShouldContain("Remove the index on status, priority?");
        await confirm.GetByTestId("remove-index-cancel").ClickAsync();
        await session.FocusAfterConfirmAsync("remove-index", "#index-row-0 [data-testid='index-remove']");

        confirm = await AskAsync(session, "index-remove", 0, "remove-index");
        await confirm.GetByTestId("remove-index-run").ClickAsync();
        await session.SnackbarAsync("Removed from the working copy");
        await session.FocusAfterConfirmAsync("remove-index", "#index-row-0 [data-testid='index-remove']");
        (await session.Page.GetByTestId("index-row").InnerTextAsync()).ShouldContain("assigned_to", Case.Sensitive,
            "the row that took the removed one's place");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_hook_names_its_point_and_the_last_one_hands_focus_to_New_hook()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("On write");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points")
            .GetByRole(AriaRole.Radio, new() { Name = "beforeCreate", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#hook-reject", "Regions are made by the operations team.");
        await session.Dialog("hook-editor").GetByTestId("hook-add").ClickAsync();
        await session.Page.GetByTestId("hook-row").WaitForAsync();

        var confirm = await AskAsync(session, "hook-remove", 0, "remove-hook");
        (await confirm.InnerTextAsync()).ShouldContain("Remove this beforeCreate hook?");
        await session.Page.Keyboard.PressAsync("Escape");
        await session.FocusAfterConfirmAsync("remove-hook", "[data-testid='hook-remove']");

        confirm = await AskAsync(session, "hook-remove", 0, "remove-hook");
        await confirm.GetByTestId("remove-hook-run").ClickAsync();
        await session.Page.GetByTestId("hook-row").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.FocusAfterConfirmAsync("remove-hook", "[data-testid='hook-new']");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_field_hands_focus_to_its_Undo()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.GetByTestId("remove-field-notes").ClickAsync();
        var confirm = session.Dialog("remove-field-sheet");
        await confirm.GetByTestId("remove-field-cancel").ClickAsync();
        await session.FocusAfterConfirmAsync("remove-field-sheet", "[data-testid='remove-field-notes']");

        await session.Page.GetByTestId("remove-field-notes").ClickAsync();
        await confirm.GetByTestId("remove-field-anyway").ClickAsync();
        await session.FocusAfterConfirmAsync("remove-field-sheet", "[data-testid='restore-field-notes']");
        session.AssertConsoleClean();
    }

    /// <summary>The Disable and the Let them back in that asked were inside the editor, so focus goes to the row.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Disabling_and_letting_back_in_hand_focus_to_the_persons_row()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");
        var person = await session.CreatePersonAsync("focus-return@example.com");
        var change = $"#change-{person}";

        await (await session.OpenPersonAsync(person)).GetByTestId("person-disable").ClickAsync();
        await session.Dialog("disable-person").GetByTestId("disable-person-cancel").ClickAsync();
        await session.FocusAfterConfirmAsync("disable-person", change);

        await (await session.OpenPersonAsync(person)).GetByTestId("person-disable").ClickAsync();
        await session.Dialog("disable-person").GetByTestId("disable-person-run").ClickAsync();
        await session.SnackbarAsync("Disabled focus-return@example.com");
        await session.FocusAfterConfirmAsync("disable-person", change);

        await (await session.OpenPersonAsync(person)).GetByTestId("person-let-in").ClickAsync();
        await session.SnackbarAsync("focus-return@example.com can sign in again");
        await session.FocusAfterConfirmAsync("disable-person", change);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Deleting_a_record_hands_focus_to_the_row_that_took_its_place()
    {
        var tenant = TenantId.New();
        await RecordEditorScenarios.SeedWorkOrderAsync(world, tenant, "WO-0901");
        await RecordEditorScenarios.SeedWorkOrderAsync(world, tenant, "WO-0902");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");

        var confirm = await RecordEditorScenarios.AskToDeleteAsync(session, "WO-0901");
        await confirm.GetByTestId("delete-record-cancel").ClickAsync();
        await session.FocusAfterConfirmAsync("delete-record", "[data-testid='grid-row'][aria-selected='true']");

        confirm = await RecordEditorScenarios.AskToDeleteAsync(session, "WO-0901");
        await confirm.GetByTestId("delete-record-run").ClickAsync();
        await session.SnackbarAsync("Record deleted");
        await session.FocusAfterConfirmAsync("delete-record", "[data-testid='grid-row'][aria-selected='true']");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// A removal confirmed after another tab moved the list removes the item it named, not the one now at its
    /// position (final review M7): the confirm holds what the index is, and finds it again when it runs.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_removal_confirmed_after_another_tab_moved_the_list_removes_what_it_named()
    {
        await using var asker = await world.SignInAsync(TestContext.Current.CancellationToken);
        await using var other = await world.SignInAsync(TestContext.Current.CancellationToken);
        await asker.GoAsync("/schema/customers");
        await asker.OpenTabAsync("Indexes");
        await asker.Page.GetByTestId("index-new").ClickAsync();
        await asker.Dialog("index-editor").GetByTestId("index-fields")
            .GetByRole(AriaRole.Button, new() { Name = "email", Exact = true }).ClickAsync();
        await asker.Dialog("index-editor").GetByTestId("index-add").ClickAsync();
        await asker.Page.GetByTestId("index-row").Nth(1).WaitForAsync();

        var confirm = await AskAsync(asker, "index-remove", 1, "remove-index");
        (await confirm.InnerTextAsync()).ShouldContain("Remove the index on email?");

        await other.GoAsync("/schema/customers");
        await other.OpenTabAsync("Indexes");
        await (await AskAsync(other, "index-remove", 0, "remove-index")).GetByTestId("remove-index-run").ClickAsync();
        await other.Page.GetByTestId("index-row").Nth(1).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await asker.Page.GetByTestId("index-row").Nth(1).WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await confirm.GetByTestId("remove-index-run").ClickAsync();
        await asker.Page.GetByTestId("index-row").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await asker.FocusAfterConfirmAsync("remove-index", "[data-testid='index-new']");
        asker.AssertConsoleClean();
    }

    /// <summary>
    /// After Keep editing, focus is back in the editor's form, so the next Escape reaches the editor and asks again
    /// (final-fix-B brief M4b): the Keep editing that had focus is gone with the question.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Keep_editing_gives_focus_back_to_the_form_so_Escape_still_asks()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        var editor = session.Dialog("new-entity");
        await session.WaitForFocusOnAsync("new-entity-name");
        await session.Page.Keyboard.TypeAsync("halfway");

        for (var asked = 0; asked < 2; asked++)
        {
            await session.Page.Keyboard.PressAsync("Escape");
            await editor.GetByTestId("editor-discard-question").WaitForAsync();
            await editor.GetByTestId("editor-keep").ClickAsync();
            await session.WaitForFocusOnAsync("new-entity-name");
        }

        (await session.Page.InputValueAsync("#new-entity-name")).ShouldBe("halfway");
        session.AssertConsoleClean();
    }

    /// <summary>Presses the <paramref name="at"/>th Remove of a list and answers its confirm.</summary>
    private static async Task<ILocator> AskAsync(AdminSession session, string remove, int at, string confirm)
    {
        await session.Page.GetByTestId(remove).Nth(at).ClickAsync();
        var dialog = session.Dialog(confirm);
        await dialog.WaitForAsync();
        return dialog;
    }

}
