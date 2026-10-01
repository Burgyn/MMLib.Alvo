using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// What is staged is drawn and choosable like what is applied (docs/todo-admin.md §8d items 22 and 28).
/// Its own world: it stages.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class StagedFieldScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// A staged field keeps its <c>default</c> and <c>indexed</c> badges, and is a candidate in the index editor
    /// before anything is applied.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_staged_field_keeps_its_badges_and_can_join_an_index()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Dialog("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("zone");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Default value" }).FillAsync("north");
        await sheet.GetByRole(AriaRole.Checkbox, new() { Name = "indexed", Exact = true }).CheckAsync();
        await sheet.GetByTestId("field-save").ClickAsync();
        await session.SnackbarAsync("Field zone added to the working copy");

        var row = session.Page.GetByTestId("field-row-zone");
        await row.WaitForAsync();
        var badges = await row.InnerTextAsync();
        badges.ShouldContain("default \"north\"");
        badges.ShouldContain("indexed");

        /* The candidates are the editor's: the tab lists what is declared, the sheet offers what can be chosen. */
        await session.OpenTabAsync("Indexes");
        await session.Page.GetByTestId("index-new").ClickAsync();
        await session.Dialog("index-editor").GetByTestId("index-fields")
            .GetByRole(AriaRole.Button, new() { Name = "zone", Exact = true }).WaitForAsync();

        session.AssertConsoleClean();
    }

    /// <summary>An entity only the working copy declares is offered as a ref target on another entity's field.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_pending_entity_can_be_pointed_at()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        var editor = session.Dialog("new-entity");
        await editor.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("tickets");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Add to the working copy" }).ClickAsync();
        await session.Page.WaitForURLAsync("**/schema/tickets");

        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Dialog("field-sheet");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "ref", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "tickets", Exact = true }).WaitForAsync();

        session.AssertConsoleClean();
    }
}
