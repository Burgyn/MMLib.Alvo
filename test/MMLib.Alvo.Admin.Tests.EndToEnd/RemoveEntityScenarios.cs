using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// An entity can be removed from the working copy, behind its own typed name, and what points at it is named first
/// (docs/todo-admin.md §8d item 24; spec §3.2, whose typed-name list names "remove entity").
/// </summary>
/// <remarks>
/// Nothing here removes an applied entity: <c>regions</c> must stay pointed at by <c>work_orders</c> for the first fact,
/// whichever order xUnit runs them in. That removal is <see cref="RemoveAppliedEntityScenarios"/>, in its own world.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RemoveEntityScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// <c>work_orders.region_id</c> points at <c>regions</c>: named, and the removal refused — no name to type, the
    /// verb disabled — and Cancel hands focus back to Remove.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_something_points_at_names_it_and_is_not_removed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.GetByTestId("remove-entity").ClickAsync();
        var confirm = session.Dialog("remove-entity-sheet");
        (await confirm.GetByTestId("entity-reference").InnerTextAsync()).ShouldContain("work_orders.region_id");
        await confirm.GetByTestId("remove-entity-blocked").WaitForAsync();
        (await confirm.GetByTestId("remove-entity-confirm").IsDisabledAsync()).ShouldBeTrue();
        (await confirm.GetByRole(AriaRole.Textbox).CountAsync()).ShouldBe(0, "a refused removal has no name to type");

        await confirm.GetByTestId("remove-entity-cancel").ClickAsync();
        await session.FocusAfterConfirmAsync("remove-entity-sheet", "[data-testid='remove-entity']");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// An entity nothing points at is removed once its name is typed; the verb leaves the screen, so focus is the list's
    /// heading, and the list no longer draws it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_nothing_points_at_is_removed_after_its_name_is_typed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Dialog("new-entity").GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("tickets");
        await session.Button("Add to the working copy").ClickAsync();
        await session.Page.WaitForURLAsync("**/schema/tickets");

        await session.Page.GetByTestId("remove-entity").ClickAsync();
        var confirm = session.Dialog("remove-entity-sheet");
        (await confirm.InnerTextAsync()).ShouldContain("Remove tickets?");
        await confirm.GetByTestId("remove-entity-unreferenced").WaitForAsync();
        var verb = confirm.GetByTestId("remove-entity-confirm");
        (await verb.IsDisabledAsync()).ShouldBeTrue("the name is not typed yet");
        await confirm.GetByRole(AriaRole.Textbox).FillAsync("ticket");
        (await verb.IsDisabledAsync()).ShouldBeTrue("a name that is not the entity's does not allow it");
        await confirm.GetByRole(AriaRole.Textbox).FillAsync("tickets");
        await verb.ClickAsync();

        await session.Page.WaitForURLAsync("**/admin/schema");
        await session.SnackbarAsync("Entity tickets removed from the working copy");
        await session.FocusAfterConfirmAsync("remove-entity-sheet", "h1");
        (await session.Page.GetByTestId("entity-row-tickets").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
}

/// <summary>
/// An applied entity removed from the working copy stays in the list, marked removed, because its table is there until
/// the apply drops it (docs/todo-admin.md §8d item 24).
/// </summary>
/// <remarks>Its own world: it removes <c>work_orders</c>, which is what points at <c>regions</c> and <c>customers</c>.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RemoveAppliedEntityScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_applied_entity_removed_from_the_copy_is_marked_removed_and_what_it_pointed_at_is_free()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("remove-entity").ClickAsync();
        var confirm = session.Dialog("remove-entity-sheet");
        (await confirm.InnerTextAsync()).ShouldContain("its table and every row in it are dropped");
        await confirm.GetByRole(AriaRole.Textbox).FillAsync("work_orders");
        await session.Page.Keyboard.PressAsync("Enter");

        await session.Page.WaitForURLAsync("**/admin/schema");
        (await session.Page.GetByTestId("entity-row-work_orders").InnerTextAsync()).ShouldContain("removed — not applied yet");

        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("entity-removed").WaitForAsync();
        (await session.Page.GetByTestId("remove-entity").CountAsync()).ShouldBe(0, "the copy no longer declares it");

        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("remove-entity").ClickAsync();
        await session.Dialog("remove-entity-sheet").GetByTestId("remove-entity-unreferenced").WaitForAsync();
        session.AssertConsoleClean();
    }
}
