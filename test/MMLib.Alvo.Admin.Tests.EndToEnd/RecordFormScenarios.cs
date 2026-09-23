using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The record form edits what a human can: a reference is chosen by name, a save says what it did, and
/// a save with nothing changed writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usability test T3 (design pass §3) is the reason.</b> "Move this order to another bike" was a
/// workaround — copy a uuid out of another table — because a reference was a raw id box. Here the
/// operator moves a work order to another customer by typing part of the customer's name, and the grid
/// then says the new name.
/// </para>
/// <para>
/// Over <c>work_orders.customer_id</c> in the field-service example, which is the reference the fixture
/// reaches. Its own world, because the seed writes rows whose unique keys another class also uses.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RecordFormScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly TenantId _tenant = TenantId.New();

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_customer_is_changed_by_typing_part_of_its_name_and_the_save_says_so()
    {
        var order = await SeedAsync();
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");

        var row = session.Page.Locator("table.a-grid tbody tr:has-text('WO-0001')");
        var edit = row.Locator("button:has-text('Edit')");
        await edit.ClickAsync();

        // --- the sheet is titled by the record, and the reference reads as the customer's name
        var sheet = session.Page.Locator("[data-testid='record-sheet']");
        await session.Page.Locator("#rf-customer_id").WaitForAsync();
        (await sheet.InnerTextAsync()).ShouldContain("Edit Service call WO-0001");
        await WaitForValueAsync(session, "#rf-customer_id", "Ada Lovelace");

        // --- a read-only field is shown, not offered: external_ref is readOnly in the descriptor
        (await session.Page.Locator("[data-testid='record-calculated']").InnerTextAsync()).ShouldContain("External ref");
        (await session.Page.Locator("#rf-external_ref").CountAsync()).ShouldBe(0);

        // --- typing part of a name lists the match; choosing it sets the reference
        await session.Page.ClickAsync("#rf-customer_id");
        await session.Page.FillAsync("#rf-customer_id", string.Empty);
        await session.Page.Keyboard.TypeAsync("grac");
        await session.Page.WaitForFunctionAsync(
            "() => document.querySelectorAll(\"[data-testid='ref-option']\").length === 1",
            null,
            new() { PollingInterval = 100 });
        var grace = session.Page.Locator("[data-testid='ref-option']");
        (await grace.InnerTextAsync()).ShouldStartWith("Grace Hopper");
        await grace.ClickAsync();
        await WaitForValueAsync(session, "#rf-customer_id", "Grace Hopper");

        // --- the save closes the sheet, says what it saved, and the grid shows the new customer
        await session.Page.ClickAsync("[data-testid='record-sheet'] button:has-text('Save')");
        var status = session.Page.Locator("[data-testid='record-status']");
        await status.Locator("text=Saved Service call WO-0001").WaitForAsync();
        (await sheet.CountAsync()).ShouldBe(0);
        await row.Locator("[data-testid='ref-cell']:has-text('Grace Hopper')").WaitForAsync();
        (await status.GetAttributeAsync("aria-live")).ShouldBe("polite");

        // --- a save with nothing changed closes without a write: the row's version does not move
        var before = await VersionAsync(order);
        await edit.ClickAsync();
        await WaitForValueAsync(session, "#rf-customer_id", "Grace Hopper");
        await session.Page.ClickAsync("[data-testid='record-sheet'] button:has-text('Save')");
        await sheet.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        (await VersionAsync(order)).ShouldBe(before, "a no-op Save must not PATCH");

        session.AssertConsoleClean();
    }

    private static async Task WaitForValueAsync(AdminSession session, string selector, string value)
        => await session.Page.WaitForFunctionAsync(
            "([selector, value]) => document.querySelector(selector)?.value === value",
            new[] { selector, value },
            new() { PollingInterval = 100 });

    /// <summary>The work order's <c>updated_at</c>, read as the system, which every write moves.</summary>
    private async Task<object?> VersionAsync(Guid order)
    {
        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var record = await data.GetAsync("work_orders", order, AlvoContext.System(_tenant));
        return record.ShouldNotBeNull()["updated_at"];
    }

    /// <summary>Gives the operator a tenant, two customers in it, and one work order for the first.</summary>
    /// <returns>The work order's id.</returns>
    private async Task<Guid> SeedAsync()
    {
        using var scope = world.Services.CreateScope();
        await FieldServiceSeed.GrantTheOperatorAsync(scope.ServiceProvider, _tenant);

        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        var region = await FieldServiceSeed.RegionAsync(data, system, "SOUTH");
        var ada = await FieldServiceSeed.CustomerAsync(data, system, _tenant, "Ada Lovelace");
        await FieldServiceSeed.CustomerAsync(data, system, _tenant, "Grace Hopper");

        return FieldServiceSeed.IdOf(await FieldServiceSeed.WorkOrderAsync(data, system, _tenant, "WO-0001", ada, region));
    }
}
