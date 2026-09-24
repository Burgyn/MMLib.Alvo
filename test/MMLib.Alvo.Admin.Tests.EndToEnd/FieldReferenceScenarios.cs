using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A field rename carries its references and plans cleanly; a removal names them first (docs/todo-admin.md §8d 16).
/// </summary>
/// <remarks>
/// <c>work_orders.assigned_to</c> is named by three rules (<c>assigned_to == @user.id</c>) and a single-field index;
/// the plan appearing is the apply's own dry run compiling the renamed rules. Its own world: it stages.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class FieldReferenceScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_rename_carries_the_rules_and_the_index_and_plans_cleanly()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("edit-field-assigned_to").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("technician_id");
        await session.Page.GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId("field-row-technician_id").WaitForAsync();
        (await session.Page.GetByTestId("rename-leftovers").CountAsync()).ShouldBe(0);

        await session.PreviewPendingAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        (await session.Content.InnerTextAsync()).ShouldContain("technician_id == @user.id");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A rule the rename cannot rewrite (a binding macro) is named on the Fields tab, and the note goes with the
    /// shell's Discard rather than outliving the edit it described.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_rename_names_what_it_could_not_carry_until_the_copy_is_discarded()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.OpenTabAsync("Rules");
        var list = session.Page.GetByRole(AriaRole.Tabpanel).GetByRole(AriaRole.Textbox).First;
        await list.FillAsync("[code].exists(c, c != '')");
        await list.PressAsync("Control+Enter");
        await session.SnackbarAsync("Rule saved to the working copy");

        await session.OpenTabAsync("Fields");
        await session.Page.GetByTestId("edit-field-code").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("region_code");
        await session.Page.GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId("field-row-region_code").WaitForAsync();
        (await session.Page.GetByTestId("rename-leftovers").InnerTextAsync()).ShouldContain("regions.rules.list");

        await session.Page.GetByTestId("pending-discard").ClickAsync();
        await session.Page.GetByTestId("discard-confirm").ClickAsync();
        await session.Page.GetByTestId("rename-leftovers").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        session.AssertConsoleClean();
    }

    /// <summary><c>work_orders.status</c> leads the composite index: the removal is named and refused, not staged.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_removal_of_an_indexed_field_names_the_index_and_stages_nothing()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("remove-field-status").ClickAsync();
        var confirm = session.Dialog("remove-field-sheet");
        (await confirm.GetByTestId("field-reference").First.InnerTextAsync()).ShouldContain("work_orders.indexes[0]");
        await confirm.GetByTestId("remove-field-blocked").WaitForAsync();
        (await confirm.GetByTestId("remove-field-anyway").IsDisabledAsync()).ShouldBeTrue("a blocked removal cannot run");

        await confirm.GetByTestId("remove-field-cancel").ClickAsync();
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("staged-status").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }
}
