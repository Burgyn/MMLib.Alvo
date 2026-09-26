using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A record form whose values could not be read refuses each field where it is, as every other form does (spec §3.8):
/// the box is invalid, the sentence follows its hint, and focus lands on the first refused field (spec §3.3).
/// </summary>
/// <remarks>
/// Over <c>work_orders.priority</c> (an integer, given a fraction its number box accepts) and <c>quoted_price</c> (a
/// decimal, given words), the two numbers the field-service example has. Its own world, because the seed writes rows whose unique keys another class also uses.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RecordFieldRefusalScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly TenantId _tenant = TenantId.New();

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Values_the_form_cannot_read_are_refused_at_their_fields_with_focus_on_the_first()
    {
        await SeedAsync();
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");
        await session.Page.GetByTestId("grid-row").Filter(new() { HasText = "WO-0001" })
            .GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();

        var sheet = session.Dialog("record-sheet");
        /* The form settles once its references resolve to names; typed before that, the values are drawn over. */
        await session.Page.WaitForFunctionAsync(
            "() => document.querySelector('#rf-customer_id')?.value === 'Ada Lovelace'", null, new() { PollingInterval = 100 });
        await sheet.Locator("#rf-priority").FillAsync("1.5");
        await sheet.Locator("#rf-quoted_price").FillAsync("a lot");
        await sheet.GetByTestId("record-save").ClickAsync();

        await session.Page.WaitForFunctionAsync(
            "() => document.querySelectorAll(\"[data-testid='field-problem']\").length === 2");
        var first = await sheet.Locator("[aria-invalid='true']").First.GetAttributeAsync("id");
        await session.Page.WaitForFunctionAsync("id => document.activeElement?.id === id", first);

        foreach (var field in new[] { "rf-priority", "rf-quoted_price" })
        {
            var control = sheet.Locator($"#{field}");
            (await control.GetAttributeAsync("aria-invalid")).ShouldBe("true");
            (await control.GetAttributeAsync("aria-describedby")).ShouldBe($"{field}-hint {field}-problem");
        }

        (await session.SnackbarCountAsync()).ShouldBe(0, "a refusal is never a snackbar");
        await sheet.Locator("#rf-priority").FillAsync("3");
        await session.Page.WaitForFunctionAsync(
            "() => document.querySelector('#rf-priority')?.getAttribute('aria-describedby') === 'rf-priority-hint'");
        session.AssertConsoleClean();
    }

    /// <summary>Gives the operator a tenant, a customer in it, and one work order for them.</summary>
    private async Task SeedAsync()
    {
        using var scope = world.Services.CreateScope();
        await FieldServiceSeed.GrantTheOperatorAsync(scope.ServiceProvider, _tenant);

        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        var region = await FieldServiceSeed.RegionAsync(data, system, "NORTH");
        var ada = await FieldServiceSeed.CustomerAsync(data, system, _tenant, "Ada Lovelace");
        await FieldServiceSeed.WorkOrderAsync(data, system, _tenant, "WO-0001", ada, region);
    }
}
