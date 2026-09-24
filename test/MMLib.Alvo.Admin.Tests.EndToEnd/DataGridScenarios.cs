using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The Data grid reads like data: a reference is the name of the row it points at, and a search
/// narrows the page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usability test T2 (design pass §3) is the reason.</b> "Which technician is on this order?"
/// failed in the UI because every reference cell was a uuid. The scenario asserts the answer is on
/// the screen — the customer's <em>name</em> under a "Customer" header — and that the name is a link
/// that opens that customer.
/// </para>
/// <para>
/// <b>Its own world, and the operator holds a tenant in it.</b> <c>work_orders</c> is tenant-scoped
/// and the bootstrap administrator holds none, so every other class sees that grid refused before a
/// row is read. Granting one through the store — the dashboard will not let an administrator grant
/// themselves one — stands in for the second administrator a real deployment has, and the rows are
/// written through the data port as that tenant, because the example ships no seed.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class DataGridScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly TenantId _tenant = TenantId.New();

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_reference_shows_the_name_it_points_at_and_a_search_narrows_the_rows()
    {
        var (ada, _) = await SeedAsync();
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");

        var rows = session.Page.Locator("table.a-grid tbody tr");
        await rows.Nth(1).WaitForAsync();

        // --- the header says what the column is, and the cell says who, not which uuid
        (await session.Page.Locator("table.a-grid th[title='customer_id']").InnerTextAsync()).Trim()
            .ShouldBe("Customer");
        var adaRow = session.Page.Locator("table.a-grid tbody tr:has-text('WO-0001')");
        var customer = adaRow.Locator("[data-testid='ref-cell']").First;
        (await customer.InnerTextAsync()).ShouldBe("Ada Lovelace");
        (await adaRow.InnerTextAsync()).ShouldNotContain(ada.ToString());

        // --- a search narrows the page to the rows that match, and clearing it brings them back
        await session.Page.Keyboard.PressAsync("/");
        (await session.Page.EvaluateAsync<string>("() => document.activeElement?.dataset.testid ?? ''"))
            .ShouldBe("grid-search", "/ focuses the search (design §5.5)");
        await session.Page.Keyboard.TypeAsync("0002");
        await session.Page.Locator("table.a-grid tbody tr:has-text('WO-0001')").WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        (await rows.CountAsync()).ShouldBe(1);
        (await rows.First.InnerTextAsync()).ShouldContain("Grace Hopper");

        await session.Page.FillAsync("[data-testid='grid-search']", string.Empty);
        await rows.Nth(1).WaitForAsync();

        // --- the name is a link to that customer, opened in its sheet on the customers screen
        await customer.ClickAsync();
        await session.Page.WaitForURLAsync($"**/admin/data/customers?record={ada}");
        await session.Page.Locator("#rf-name").WaitForAsync();
        (await session.Page.InputValueAsync("#rf-name")).ShouldBe("Ada Lovelace");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A link to a record this caller cannot read says so, rather than opening nothing silently.
    /// </summary>
    /// <remarks>
    /// Over <c>regions</c>, which is global, so the answer is about the record and not about a tenant.
    /// The same words cover "does not exist" and "a rule excludes it", because the data port answers
    /// both the same way on purpose.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_link_to_a_record_nobody_can_read_says_so()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync($"/data/regions?record={Guid.NewGuid()}");

        var note = session.Page.Locator("[data-testid='record-unreachable']");
        await note.WaitForAsync();
        (await note.InnerTextAsync()).ShouldContain("not one this credential can read");
        (await session.Page.Locator("#rf-name").CountAsync()).ShouldBe(0, "no sheet opens over nothing");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// <c>j j Enter</c> opens the second row, the keys stay with the sheet while it is open, and closing it
    /// gives focus back to the row (design §5.5; the WAI-ARIA dialog pattern).
    /// </summary>
    /// <remarks>
    /// Over <c>regions</c>, which is global, so no tenant is needed; its own two rows, so there are always at
    /// least two whatever else this world has seeded, and it asserts "the second row" — whatever the entity's
    /// order puts there — rather than a name.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task J_moves_the_selected_row_and_enter_opens_it()
    {
        await SeedRegionsAsync("KEYS-A", "KEYS-B");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/regions");

        var rows = session.Page.Locator("[data-testid='grid-row']");
        var selected = session.Page.Locator("[data-testid='grid-row'][aria-selected='true']");
        await rows.Nth(1).WaitForAsync();
        await session.Page.Locator("[data-testid='record-grid'][data-alvo-keyboard='ready']").WaitForAsync();

        // --- j j selects the second row, focuses it, and draws it as selected
        await session.Page.Keyboard.PressAsync("j");
        await session.Page.Keyboard.PressAsync("j");
        await session.Page.WaitForFunctionAsync(SecondRowHasFocus);
        (await rows.Nth(1).GetAttributeAsync("aria-selected")).ShouldBe("true");
        (await selected.CountAsync()).ShouldBe(1);
        (await rows.Nth(1).EvaluateAsync<string>("row => getComputedStyle(row).boxShadow"))
            .ShouldNotBe("none", "the selected row carries the focus ring");

        // --- Enter opens that row's sheet
        var second = await rows.Nth(1).InnerTextAsync();
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.Locator("#rf-name").WaitForAsync();
        second.ShouldContain(await session.Page.InputValueAsync("#rf-name"));

        /* --- while the sheet is open, k is the sheet's. From the second row a k that reached the grid would
               select the first, so an unchanged selection is the guard working, not a clamp at an end. */
        await session.Page.Locator("[data-testid='record-sheet'] [role='dialog']").FocusAsync();
        await session.Page.Keyboard.PressAsync("k");
        await session.Page.WaitForTimeoutAsync(500);
        (await rows.Nth(1).GetAttributeAsync("aria-selected")).ShouldBe("true");
        (await selected.CountAsync()).ShouldBe(1);

        // --- closing the sheet gives focus back to the row that opened it
        await session.Page.Keyboard.PressAsync("Escape");
        await session.Page.Locator("#rf-name").WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        await session.Page.WaitForFunctionAsync(SecondRowHasFocus);

        session.AssertConsoleClean();
    }

    private const string SecondRowHasFocus
        = "() => document.activeElement === document.querySelectorAll(\"[data-testid='grid-row']\")[1]";

    /// <summary>Writes two regions, each of which must be unique across the world.</summary>
    private async Task SeedRegionsAsync(params string[] codes)
    {
        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        foreach (var code in codes)
        {
            await FieldServiceSeed.RegionAsync(data, AlvoContext.System(_tenant), code);
        }
    }

    /// <summary>
    /// Gives the operator a tenant and writes two work orders into it, one per customer.
    /// </summary>
    /// <returns>The two customers' ids.</returns>
    private async Task<(Guid Ada, Guid Grace)> SeedAsync()
    {
        using var scope = world.Services.CreateScope();
        await FieldServiceSeed.GrantTheOperatorAsync(scope.ServiceProvider, _tenant);

        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        var region = await FieldServiceSeed.RegionAsync(data, system, "NORTH");
        var ada = await FieldServiceSeed.CustomerAsync(data, system, _tenant, "Ada Lovelace");
        var grace = await FieldServiceSeed.CustomerAsync(data, system, _tenant, "Grace Hopper");

        await FieldServiceSeed.WorkOrderAsync(data, system, _tenant, "WO-0001", ada, region);
        await FieldServiceSeed.WorkOrderAsync(data, system, _tenant, "WO-0002", grace, region);

        return (FieldServiceSeed.IdOf(ada), FieldServiceSeed.IdOf(grace));
    }
}
