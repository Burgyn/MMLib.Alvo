using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The Schema screen's map draws the working copy — every entity, every reference, what is not applied yet
/// and, on request, what each entity reacts with — and every box is a way into its entity.
/// </summary>
/// <remarks>
/// <b>Its own world</b>: two scenarios stage an entity and a hook in this operator's working copy, which every
/// other class in the same world would see. The world boots <c>examples/field-service</c>: <c>regions</c>,
/// <c>customers</c> and <c>work_orders</c>, which refers to the other two.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class SystemMapScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_map_draws_every_entity_and_every_reference()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        await session.Page.GetByRole(AriaRole.Radio, new() { Name = "Map", Exact = true }).ClickAsync();
        var map = session.Page.GetByTestId("system-map");
        await map.WaitForAsync();

        foreach (var entity in new[] { "regions", "customers", "work_orders" })
        {
            await Box(session, entity).WaitForAsync();
        }

        (await map.TextContentAsync() ?? string.Empty).ShouldContain("work_orders.customer_id points at customers");
        (await Box(session, "customers").TextContentAsync() ?? string.Empty).ShouldContain("tenant · 5 fields");
        session.Page.Url.ShouldContain("view=map");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_box_opens_its_entity()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema?view=map");

        await Box(session, "regions").ClickAsync();

        await session.Page.WaitForURLAsync("**/schema/regions");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// The map reads the working copy, not the applied schema: an entity added a moment ago is on it, and
    /// says it is not applied.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_staged_entity_is_on_the_map_before_any_apply()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "New entity", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "invoices");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Add to the working copy" }).ClickAsync();
        await session.Page.WaitForURLAsync("**/schema/invoices");

        await session.GoAsync("/schema?view=map");

        (await Box(session, "invoices").GetAttributeAsync("aria-label") ?? string.Empty).ShouldContain("not applied");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// The reactions layer is in the address, so a reload — or a link somebody sent — draws what was on the
    /// screen, guard rows included.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_reactions_layer_is_addressable_and_survives_a_reload()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await StageRefusalAsync(session);

        await session.GoAsync("/schema?view=map");
        await Reactions(session).CheckAsync();
        await session.Page.WaitForURLAsync("**layers=reactions**");

        await session.Page.ReloadAsync();
        await session.SettleAsync();

        (await Reactions(session).IsCheckedAsync()).ShouldBeTrue();
        await session.Page.GetByTestId("system-map").WaitForAsync();
        (await Box(session, "work_orders").TextContentAsync() ?? string.Empty).ShouldContain("1 refuse");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// A map is wider than a phone, and it scrolls inside its own panel — never the page.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_map_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/schema?view=map&layers=reactions");
        await session.Page.GetByTestId("system-map").WaitForAsync();

        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    private static ILocator Box(AdminSession session, string entity)
        => session.Page.GetByTestId("system-map").GetByRole(AriaRole.Link, new() { Name = entity, Exact = false });

    private static ILocator Reactions(AdminSession session)
        => session.Page.GetByRole(AriaRole.Checkbox, new() { Name = "Show reactions" });

    /// <summary>Stages a before-update refusal on <c>work_orders</c> from its On write tab.</summary>
    private static async Task StageRefusalAsync(AdminSession session)
    {
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        var before = await session.Page.GetByTestId("hook-row").CountAsync();
        await session.Page.GetByTestId("hook-points")
            .GetByRole(AriaRole.Radio, new() { Name = "beforeUpdate", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#hook-condition", "old.status == 'completed'");
        await session.Page.FillAsync("#hook-reject", "A completed work order cannot be reopened.");
        await session.Page.GetByTestId("hook-add").ClickAsync();
        await session.Page.GetByTestId("hook-row").Nth(before).WaitForAsync();
    }
}
