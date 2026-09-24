using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A rollup is authored from the field editor, and the apply accepts what it composes (docs/todo-admin.md §7).
/// </summary>
/// <remarks>
/// <c>customers ← work_orders.customer_id</c> is the field-service pair both sides of which are scoped; the plan
/// appearing on Preview is the apply's own dry run accepting the declaration. Its own world: it stages.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class MaintainedFieldScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_count_rollup_is_composed_reopened_as_a_rollup_and_planned()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("open_orders");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "rollup", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "work_orders", Exact = true }).ClickAsync();

        (await sheet.GetByTestId("rollup-type").InnerTextAsync()).ShouldContain("integer");
        (await sheet.GetByRole(AriaRole.Checkbox, new() { Name = "required", Exact = true }).CountAsync()).ShouldBe(0);
        await sheet.GetByTestId("rollup-where-refused").WaitForAsync();
        await session.Page.GetByTestId("field-save").ClickAsync();

        var row = session.Page.GetByTestId("field-row-open_orders");
        await row.WaitForAsync();
        (await row.InnerTextAsync()).ShouldContain("rollup");

        await session.Page.GetByTestId("edit-field-open_orders").ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "rollup", Exact = true, Checked = true }).WaitForAsync();
        await session.Page.GetByTestId("sheet-close").ClickAsync();

        await session.PreviewPendingAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        (await session.Content.InnerTextAsync()).ShouldContain("\"from\": \"work_orders\"");

        session.AssertConsoleClean();
    }

    /// <summary><c>regions</c> is global and <c>work_orders</c> scoped: the source is said, not offered.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_source_across_tenancy_is_said_and_not_offered()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "rollup", Exact = true }).ClickAsync();

        (await sheet.GetByTestId("rollup-refused-work_orders").InnerTextAsync()).ShouldContain("tenancy");
        (await sheet.GetByRole(AriaRole.Radio, new() { Name = "work_orders", Exact = true }).CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }
}

/// <summary>
/// A computed field is authored and keeps its kind once staged. It stops before Preview: whether the SQLite
/// migrator can add a stored generated column to an existing table is the migrator's question, not the editor's.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class ComputedFieldScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_computed_field_is_composed_and_reopened_as_computed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("priority_twice");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "integer", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Expression" }).FillAsync("priority + priority");
        await session.Page.GetByTestId("field-save").ClickAsync();

        var row = session.Page.GetByTestId("field-row-priority_twice");
        await row.WaitForAsync();
        (await row.InnerTextAsync()).ShouldContain("computed");

        await session.Page.GetByTestId("edit-field-priority_twice").ClickAsync();
        (await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Expression" }).InputValueAsync()).ShouldBe("priority + priority");

        session.AssertConsoleClean();
    }
}
