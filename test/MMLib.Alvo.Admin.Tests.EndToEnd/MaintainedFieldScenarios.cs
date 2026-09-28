using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Data;

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
        await session.Dialog("field-sheet").GetByTestId("editor-cancel").ClickAsync();

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
/// A computed field is authored and keeps its kind once staged. It stops before Preview; applying one to populated
/// entities is <see cref="ComputedOnPopulatedEntityScenarios"/>' case.
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

/// <summary>
/// A computed expression the generated column cannot carry is refused by Preview's dry run as the descriptor
/// validator's own finding at the field — never the generic "Something went wrong" of an exception nothing maps.
/// </summary>
/// <remarks>
/// The field editor has no CEL compiler (<c>FieldFacets.ComputedTypes</c> gives the reason), so it stages
/// <c>now()</c> as typed; the refusal is the build's, and this pins that it arrives as one. Its own world: it stages.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ComputedRefusalScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_computed_field_calling_now_is_refused_at_preview_naming_the_field()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("date_test");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "datetime", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Expression" }).FillAsync("now()");
        await session.Page.GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId("field-row-date_test").WaitForAsync();

        await session.Page.ClickAsync("[data-testid='pending-preview']");
        var refusal = session.Page.GetByTestId("error-panel");
        await refusal.WaitForAsync();

        var said = await refusal.InnerTextAsync();
        said.ShouldContain("The descriptor is not valid");
        said.ShouldContain("/entities/customers/fields/date_test/computed");
        said.ShouldNotContain("Something went wrong");
        (await session.Page.GetByTestId("plan").CountAsync()).ShouldBe(0, "a refused descriptor has no plan");

        session.AssertConsoleClean();
    }
}

/// <summary>
/// A computed field is added to entities that already hold rows — a parent other rows reference, and the child that
/// references it — and the apply keeps every row and computes the value for each.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rows are written before the field is staged, and that is the case under test.</b> SQLite's <c>ALTER TABLE
/// … ADD COLUMN</c> cannot add a <c>STORED</c> generated column to a table that holds a row, so the migrator has to
/// rebuild the table; on an empty one the question never arises. <c>customers</c> is the parent every work order
/// references, so a rebuild that let the foreign key act would take the work order with it.
/// </para>
/// <para>Its own world: it applies.</para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ComputedOnPopulatedEntityScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly TenantId _tenant = TenantId.New();

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_computed_field_added_to_populated_entities_is_applied_and_computed_for_every_row()
    {
        var (customer, workOrder) = await SeedAsync();
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);

        await AddComputedAsync(session, "customers", "display_name", "string", "name");
        await AddComputedAsync(session, "work_orders", "priority_twice", "integer", "priority + priority");
        await session.PreviewPendingAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        await session.Page.FillAsync("#apply-reason", "Computed fields over populated entities");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Apply these changes" }).ClickAsync();
        await session.Content.GetByText("Applied as revision").First.WaitForAsync();

        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        var customerNow = (await data.GetAsync("customers", customer, system, TestContext.Current.CancellationToken)).ShouldNotBeNull("the parent row survived the rebuild");
        customerNow["display_name"].ShouldBe("Customer of WO-0901");
        var workOrderNow = (await data.GetAsync("work_orders", workOrder, system, TestContext.Current.CancellationToken))
            .ShouldNotBeNull("the child row survived its parent's rebuild");
        Convert.ToInt64(workOrderNow["priority_twice"], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(6);
        workOrderNow["customer_id"].ShouldBe(customer, "and still references its customer");

        session.AssertConsoleClean();
    }

    private static async Task AddComputedAsync(AdminSession session, string entity, string name, string type, string expression)
    {
        await session.GoAsync($"/schema/{entity}");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync(name);
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = type, Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Expression" }).FillAsync(expression);
        await session.Page.GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId($"field-row-{name}").WaitForAsync();
    }

    private async Task<(Guid Customer, Guid WorkOrder)> SeedAsync()
    {
        using var scope = world.Services.CreateScope();
        await FieldServiceSeed.GrantTheOperatorAsync(scope.ServiceProvider, _tenant);
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        var region = await FieldServiceSeed.RegionAsync(data, system, "R-WO-0901");
        var customer = await FieldServiceSeed.CustomerAsync(data, system, _tenant, "Customer of WO-0901");
        var workOrder = await FieldServiceSeed.WorkOrderAsync(data, system, _tenant, "WO-0901", customer, region);
        return (Id(customer), Id(workOrder));
    }

    private static Guid Id(AlvoRecord record) => record["id"] switch
    {
        Guid id => id,
        var other => Guid.Parse(Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)!),
    };
}
