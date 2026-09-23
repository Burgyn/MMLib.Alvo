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
    /// Gives the operator a tenant and writes two work orders into it, one per customer.
    /// </summary>
    /// <returns>The two customers' ids.</returns>
    private async Task<(Guid Ada, Guid Grace)> SeedAsync()
    {
        using var scope = world.Services.CreateScope();
        await GrantTheOperatorATenantAsync(scope.ServiceProvider);

        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        var region = await data.CreateAsync(
            "regions", new Dictionary<string, object?> { ["code"] = "NORTH", ["name"] = "North" }, system);
        var ada = await CustomerAsync(data, system, "Ada Lovelace");
        var grace = await CustomerAsync(data, system, "Grace Hopper");

        await WorkOrderAsync(data, system, "WO-0001", ada, region);
        await WorkOrderAsync(data, system, "WO-0002", grace, region);

        return (IdOf(ada), IdOf(grace));
    }

    private static async Task GrantTheOperatorATenantAsync(IServiceProvider services)
    {
        var people = services.GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey);
        var page = await people.ListAsync(new AlvoUserQuery());
        var operatorAccount = page.Users.Single(
            person => string.Equals(person.Email, AdminWorld.AdminEmail, StringComparison.OrdinalIgnoreCase));

        await people.SetTenantAsync(operatorAccount.Id, _tenant);
    }

    private static Task<AlvoRecord> CustomerAsync(IAlvoData data, AlvoContext context, string name)
        => data.CreateAsync(
            "customers", new Dictionary<string, object?> { ["tenant_id"] = _tenant.Value, ["name"] = name, ["tier"] = "standard" },
            context);

    private static Task<AlvoRecord> WorkOrderAsync(
        IAlvoData data, AlvoContext context, string reference, AlvoRecord customer, AlvoRecord region)
        => data.CreateAsync(
            "work_orders",
            new Dictionary<string, object?>
            {
                ["tenant_id"] = _tenant.Value,
                ["reference"] = reference,
                ["title"] = $"Service call {reference}",
                ["status"] = "scheduled",
                ["priority"] = 3,
                ["access_code"] = "1234",
                ["customer_id"] = IdOf(customer),
                ["region_id"] = IdOf(region),
            },
            context);

    private static Guid IdOf(AlvoRecord record) => record["id"] switch
    {
        Guid id => id,
        var other => Guid.Parse(other!.ToString()!),
    };
}
