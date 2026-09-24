using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The entity list is filtered, and the entity's collections are added to through editors (spec §3.1, §3.2, §3.4).
/// </summary>
/// <remarks>Its own world: it stages entities and indexes into the operator's one working copy.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class SchemaListScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task New_entity_is_an_editor_that_opens_on_its_name_and_adds_on_Enter()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        await session.Button("New entity", exact: true).ClickAsync();
        await session.Dialog("new-entity").WaitForAsync();
        (await session.FocusedAsync()).ShouldStartWith("input#new-entity-name");

        await session.Page.Keyboard.TypeAsync("tickets");
        await session.Page.Keyboard.PressAsync("Enter");

        await session.Page.WaitForURLAsync("**/schema/tickets");
        await session.SnackbarAsync("Added tickets to the working copy");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_half_named_entity_is_not_lost_to_an_Escape()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "invo");

        await session.Page.Keyboard.PressAsync("Escape");

        await session.Dialog("new-entity").GetByTestId("editor-discard-question").WaitForAsync();
        await session.Dialog("new-entity").GetByTestId("editor-keep").ClickAsync();
        (await session.Page.InputValueAsync("#new-entity-name")).ShouldBe("invo");
    }

    /// <summary>An untouched editor closes on Escape without a question, and focus goes back to its trigger.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_untouched_new_entity_closes_on_Escape_and_gives_focus_back()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        var trigger = session.Button("New entity", exact: true);
        await trigger.ClickAsync();
        await session.Dialog("new-entity").WaitForAsync();

        await session.Page.Keyboard.PressAsync("Escape");

        await session.Page.GetByTestId("new-entity").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.WaitForFunctionAsync("() => document.activeElement?.textContent?.trim() === 'New entity'");
    }

    /// <summary>A name the schema cannot carry is an alert in the editor, focused on every attempt.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_name_is_an_alert_that_takes_focus_on_every_attempt()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "Bad Name");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await session.Page.Locator("#new-entity-name").FocusAsync();
            await session.Page.Keyboard.PressAsync("Enter");
            await session.Page.WaitForFunctionAsync(
                "() => document.activeElement?.closest(\"[data-testid='error-panel']\") !== null");
        }

        (await session.SnackbarCountAsync()).ShouldBe(0, "an error is never a snackbar");
        session.Page.Url.ShouldEndWith("/schema");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_filter_narrows_the_list_and_says_when_nothing_matches()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        await session.Page.GetByLabel("Filter entities").FillAsync("work");

        await session.Page.GetByTestId("entity-row-customers").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("entity-row-work_orders").IsVisibleAsync()).ShouldBeTrue();

        await session.Page.GetByLabel("Filter entities").FillAsync("zzz");
        await session.Content.GetByText("Nothing matches").WaitForAsync();
    }

    /// <summary>
    /// A created entity opens on its own screen, and is in the list where the list puts a staged one: first, in view.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_created_entity_is_listed_in_place_and_in_view()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/schema");
        await session.Page.GetByTestId("pagehead-overflow").ClickAsync();
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "depots");
        await session.Dialog("new-entity").GetByRole(AriaRole.Button, new() { Name = "Add to the working copy" }).ClickAsync();
        await session.Page.WaitForURLAsync("**/schema/depots");

        await session.Page.GoBackAsync();

        var row = session.Page.GetByTestId("entity-row-depots");
        await row.WaitForAsync();
        await session.WaitForInViewAsync(row);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_system_map_is_still_one_choice_away()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        await session.Page.GetByRole(AriaRole.Radio, new() { Name = "Map", Exact = true }).ClickAsync();

        await session.Page.GetByTestId("system-map").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_index_is_added_through_an_editor_and_a_double_click_adds_one()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("Indexes");
        var before = await session.Page.GetByTestId("index-row").CountAsync();

        await session.Page.GetByTestId("index-new").ClickAsync();
        var editor = session.Dialog("index-editor");
        await editor.GetByTestId("index-fields")
            .GetByRole(AriaRole.Button, new() { Name = "name", Exact = true }).ClickAsync();
        await editor.GetByTestId("index-add").DblClickAsync();

        await session.SnackbarAsync("Index added to the working copy");
        (await session.Page.GetByTestId("index-row").CountAsync()).ShouldBe(before + 1);
    }

    /// <summary>
    /// A new index appears where the list puts it and is scrolled to, and focus goes back to the button that opened
    /// the editor (spec §3.4, §3.5).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_index_is_scrolled_into_view_and_focus_returns_to_Add_index()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.Page.SetViewportSizeAsync(375, 420);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("Indexes");
        var before = await session.Page.GetByTestId("index-row").CountAsync();

        await session.Page.GetByTestId("index-new").ClickAsync();
        var editor = session.Dialog("index-editor");
        await editor.GetByTestId("index-fields")
            .GetByRole(AriaRole.Button, new() { Name = "priority", Exact = true }).ClickAsync();
        await editor.GetByTestId("index-add").ClickAsync();
        await session.SnackbarAsync("Index added to the working copy");

        var added = session.Page.GetByTestId("index-row").Nth(before);
        await added.WaitForAsync();
        await session.WaitForInViewAsync(added);
        await session.Page.WaitForFunctionAsync("() => document.activeElement?.dataset.testid === 'index-new'");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_staged_index_cannot_happen_without_its_confirm()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("Indexes");
        await session.Page.GetByTestId("index-new").ClickAsync();
        await session.Dialog("index-editor").GetByTestId("index-fields")
            .GetByRole(AriaRole.Button, new() { Name = "name", Exact = true }).ClickAsync();
        await session.Dialog("index-editor").GetByTestId("index-add").ClickAsync();
        /* Counted once the add has landed: the click returns before the re-render that draws the row. */
        await session.SnackbarAsync("Index added to the working copy");
        var rows = await session.Page.GetByTestId("index-row").CountAsync();

        await session.Page.GetByTestId("index-remove").Last.ClickAsync();
        await session.Dialog("remove-index").GetByTestId("remove-index-cancel").ClickAsync();
        await session.Page.GetByTestId("remove-index").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("index-row").CountAsync()).ShouldBe(rows);

        await session.Page.GetByTestId("index-remove").Last.ClickAsync();
        await session.Dialog("remove-index").GetByTestId("remove-index-run").ClickAsync();
        await session.SnackbarAsync("Removed from the working copy");
    }

    /// <summary>A double click on the confirm's verb removes one index, not two (spec §3.4).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_Remove_index_removes_one()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("Indexes");
        for (var added = 0; added < 2; added++)
        {
            await session.Page.GetByTestId("index-new").ClickAsync();
            await session.Dialog("index-editor").GetByTestId("index-fields")
                .GetByRole(AriaRole.Button, new() { Name = "email", Exact = true }).ClickAsync();
            await session.Dialog("index-editor").GetByTestId("index-add").ClickAsync();
            await session.Page.GetByTestId("index-editor").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }

        var rows = await session.Page.GetByTestId("index-row").CountAsync();
        await session.Page.GetByTestId("index-remove").Last.ClickAsync();
        await session.Dialog("remove-index").GetByTestId("remove-index-run").DblClickAsync();

        await session.Page.GetByTestId("remove-index").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.SettleAsync();
        (await session.Page.GetByTestId("index-row").CountAsync()).ShouldBe(rows - 1);
        (await session.SnackbarCountAsync("Removed from the working copy")).ShouldBe(1);
    }
}
