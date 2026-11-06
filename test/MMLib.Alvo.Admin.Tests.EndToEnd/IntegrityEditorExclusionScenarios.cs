using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// HX-12's dashboard slice: an exclusion with a <c>where</c> is declared on the Indexes tab — the filter checked live
/// against the real host — applied, and a write that would overlap is refused on the Data screen with the fix.
/// </summary>
/// <remarks>
/// The bike-workshop <c>rentals</c>: no bike is rented twice over overlapping <c>[starts_at, due_at)</c> unless the
/// rental is cancelled. Its own world: it applies.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class IntegrityEditorExclusionScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    private static readonly DateTimeOffset _day = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task IntegrityEditor_an_exclusion_with_where_is_saved_applied_and_refuses_an_overlap()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await DeclareTheExclusionAsync(session);
        await session.PreviewPendingAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        await session.Page.FillAsync("#apply-reason", "No bike is rented twice at once");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Apply these changes" }).ClickAsync();
        await session.Content.GetByText("Applied as revision").First.WaitForAsync();

        await SeedTwoRentalsAsync();
        await session.GoAsync("/data/rentals");
        await session.Page.GetByTestId("grid-row").Filter(new() { HasText = "reserved" })
            .GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        var form = session.Dialog("record-sheet");
        var starts = form.Locator("#rf-starts_at");
        await Assertions.Expect(starts).Not.ToHaveValueAsync(string.Empty);
        await starts.FillAsync("2026-12-01T11:00");
        await form.GetByTestId("record-save").ClickAsync();

        var refusal = form.GetByTestId("error-panel");
        await refusal.WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("Choose a range that does not overlap one another record already holds");

        session.AssertConsoleClean();
    }

    /// <summary>Adds <c>{fields: [fleet_bike_id], exclude: {starts_at, due_at}, where: status != 'cancelled'}</c> on the tab.</summary>
    private static async Task DeclareTheExclusionAsync(AdminSession session)
    {
        await session.GoAsync("/schema/rentals");
        await session.OpenTabAsync("Indexes");
        await session.Page.GetByTestId("index-new").ClickAsync();
        var editor = session.Dialog("index-editor");
        await editor.GetByTestId("index-fields").GetByRole(AriaRole.Button, new() { Name = "fleet_bike_id", Exact = true }).ClickAsync();
        await editor.GetByRole(AriaRole.Checkbox, new() { Name = "Exclude overlaps", Exact = false }).CheckAsync();
        await Assertions.Expect(editor.GetByTestId("index-unique")).ToHaveCountAsync(0);
        await editor.GetByTestId("index-exclude-from").GetByRole(AriaRole.Radio, new() { Name = "starts_at", Exact = true }).ClickAsync();
        await editor.GetByTestId("index-exclude-to").GetByRole(AriaRole.Radio, new() { Name = "due_at", Exact = true }).ClickAsync();

        var where = editor.GetByTestId("index-where-box");
        await where.FillAsync("missing == 'x'");
        var finding = session.Page.GetByTestId("check-index-where").First;
        await finding.WaitForAsync(new() { Timeout = 5_000 });
        (await finding.InnerTextAsync()).ShouldContain("missing");
        await where.FillAsync("status != 'cancelled'");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 5_000 });

        await editor.GetByTestId("index-add").ClickAsync();
        await session.Page.GetByTestId("index-exclude").WaitForAsync();
        (await session.Page.GetByTestId("index-where").InnerTextAsync()).ShouldContain("status != 'cancelled'");
    }

    /// <summary>One bike, one customer, an active rental 10:00–12:00 and a reserved one 13:00–14:00 on the same day.</summary>
    private async Task SeedTwoRentalsAsync()
    {
        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(tenant: null);
        var customer = await CreateAsync(data, system, "customers", new()
        {
            ["first_name"] = "Eva",
            ["last_name"] = "Horváthová",
            ["phone"] = "+421 905 100 400",
        });
        var bike = await CreateAsync(data, system, "rental_fleet", new()
        {
            ["code"] = "RENT-101",
            ["brand"] = "Kellys",
            ["model"] = "Phanatic",
            ["category"] = "ebike",
            ["daily_rate"] = 35m,
            ["deposit"] = 200m,
        });
        await CreateAsync(data, system, "rentals", Rental(bike, customer, "active", 10, 12));
        await CreateAsync(data, system, "rentals", Rental(bike, customer, "reserved", 13, 14));
    }

    private static Dictionary<string, object?> Rental(object bike, object customer, string status, int from, int to) => new(StringComparer.Ordinal)
    {
        ["fleet_bike_id"] = bike,
        ["customer_id"] = customer,
        ["status"] = status,
        ["starts_at"] = _day.AddHours(from),
        ["due_at"] = _day.AddHours(to),
        ["days"] = 0.5m,
        ["daily_rate"] = 35m,
    };

    private static async Task<object> CreateAsync(IAlvoData data, AlvoContext system, string entity, Dictionary<string, object?> values)
        => (await data.CreateAsync(entity, values, system, cancellationToken: TestContext.Current.CancellationToken))["id"]!;
}
