using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A delivery's steps, its sleep and its supersession, as the Operations page shows them (spec 2026-10-14 HX-14).</summary>
/// <param name="world">The field-service world with a stepped and a debounced region handler, and a browser.</param>
public sealed class DeliveryStepsScenarios(DeliveryStepsWorld world) : IClassFixture<DeliveryStepsWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_delivery_that_failed_at_its_third_step_lists_the_two_done_steps()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await world.SignInAsync(ct);
        await CreateRegionAsync("STEP-1", ct);
        await session.GoAsync("/operations");
        await Assertions.Expect(session.Page.GetByTestId($"ops-handler-concurrency-{DeliveryStepsWorld.ConfirmRegion}")).ToHaveTextAsync("per tenant · 2");

        await WaitForRowAsync(session, "retrying", DeliveryStepsWorld.ConfirmRegion, "the stepped handler failed its third step within 15 seconds");
        await RowOf(session, DeliveryStepsWorld.ConfirmRegion).ClickAsync();

        var steps = session.Page.GetByRole(AriaRole.List, new() { Name = "Steps" });
        await Assertions.Expect(steps.GetByRole(AriaRole.Listitem)).ToHaveCountAsync(2);
        await Assertions.Expect(steps).ToContainTextAsync("charge-deposit");
        await Assertions.Expect(steps).ToContainTextAsync("send-confirmation");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_superseded_delivery_reads_superseded_and_offers_no_run_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await world.SignInAsync(ct);
        var tenant = TenantId.New();
        var data = world.Services.GetRequiredService<IAlvoData>();
        var customer = await FieldServiceSeed.CustomerAsync(data, AlvoContext.System(tenant), tenant, "Jana");
        await RenameAsync(data, tenant, customer, "Jana Nová", ct);
        await RenameAsync(data, tenant, customer, "Jana Nováková", ct);
        await session.GoAsync("/operations");

        await session.Page.GetByTestId("ops-status-filter-superseded").ClickAsync();
        await WaitForRowAsync(session, "superseded", DeliveryStepsWorld.RenameCustomer, "the older debounced delivery was superseded within 15 seconds");
        await RowOf(session, DeliveryStepsWorld.RenameCustomer).ClickAsync();

        await Assertions.Expect(session.Page.GetByTestId("ops-selected-status")).ToHaveTextAsync("superseded");
        await Assertions.Expect(session.Page.GetByTestId("ops-delivery-no-steps")).ToBeVisibleAsync();
        await Assertions.Expect(session.Page.GetByTestId("ops-redeliver")).ToHaveCountAsync(0);
        session.AssertConsoleClean();
    }

    private static ILocator RowOf(AdminSession session, string subscriber) =>
        session.Page.GetByTestId("ops-delivery-list").Locator("[data-testid^='ops-row-']").Filter(new() { HasText = subscriber }).First;

    private static async Task WaitForRowAsync(AdminSession session, string status, string subscriber, string because)
    {
        for (var poll = 0; poll < 30; poll++)
        {
            if (await StatusAsync(RowOf(session, subscriber)) == status)
            {
                return;
            }

            await session.Page.WaitForTimeoutAsync(500);
            await session.Page.GetByTestId("ops-refresh").ClickAsync();
        }

        (await StatusAsync(RowOf(session, subscriber))).ShouldBe(status, because);
    }

    private static async Task<string?> StatusAsync(ILocator row) =>
        await row.CountAsync() == 0 ? null : (await row.GetByTestId("ops-delivery-status").InnerTextAsync()).Trim();

    private async Task<AlvoRecord> CreateRegionAsync(string code, CancellationToken ct) => await world.Services.GetRequiredService<IAlvoData>().CreateAsync(
        "regions", new Dictionary<string, object?> { ["code"] = code, ["name"] = code }, AlvoContext.System(null), cancellationToken: ct);

    private static Task<AlvoRecord> RenameAsync(IAlvoData data, TenantId tenant, AlvoRecord customer, string name, CancellationToken ct) => data.UpdateAsync(
        "customers", FieldServiceSeed.IdOf(customer), new Dictionary<string, object?> { ["name"] = name }, AlvoContext.System(tenant), cancellationToken: ct);
}
