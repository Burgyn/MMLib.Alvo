using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// An apply made in one tab reaches another tab that was already open, on its next navigation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins</b> (docs/architecture/admin-dashboard-review.md, F-15): the management gateway is
/// one per circuit, caches the descriptor, and dropped that cache only on an apply its own circuit made. A tab
/// opened before somebody else's apply kept the old revision on its project card, and on every screen, until
/// the browser reloaded — however far the operator navigated.
/// </para>
/// <para>
/// <b>Two browser contexts, so two circuits</b>, and the one that looks is moved by a click on its own
/// navigation rather than by <c>GoAsync</c>: a page load starts a new circuit, which never had the stale
/// cache, and would pass with the defect in place. <b>Its own world</b>, because it applies, and the other
/// classes read the revision the world was seeded at.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class OtherCircuitApplyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_tab_open_before_another_tabs_apply_reads_the_new_revision_on_its_next_navigation()
    {
        var cancel = TestContext.Current.CancellationToken;
        await using var bystander = await world.SignInAsync(cancel);
        await bystander.GoAsync("/schema");
        var before = await Card(bystander).InnerTextAsync();

        await using var applier = await world.SignInAsync(cancel);
        var applied = await ApplyNewEntityAsync(applier, "tickets");

        /* The premise: nothing told the bystander's circuit. Its card still reads what it read at load. */
        (await Card(bystander).InnerTextAsync()).ShouldBe(before);

        await bystander.Page.Locator("nav.a-sidebar")
            .GetByRole(AriaRole.Link, new() { Name = "Configuration history", Exact = true }).ClickAsync();
        await bystander.Page.WaitForURLAsync("**/history");

        await bystander.Page.Locator($"[data-testid='project-card']:has-text('revision {applied}')").WaitForAsync();

        bystander.AssertConsoleClean();
        applier.AssertConsoleClean();
    }

    private static ILocator Card(AdminSession session) => session.Page.Locator("[data-testid='project-card']");

    /// <summary>Stages one entity, plans it and applies it, and answers with the revision it became.</summary>
    private static async Task<string> ApplyNewEntityAsync(AdminSession session, string name)
    {
        await session.GoAsync("/schema");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "New entity", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", name);
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Add to the working copy" }).ClickAsync();
        await session.Page.WaitForURLAsync($"**/schema/{name}");

        await session.GoAsync("/changes");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Plan this change" }).ClickAsync();
        await session.Page.GetByText("against the database").First.WaitForAsync();
        await session.Page.FillAsync("#apply-reason", $"Add {name}");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Apply these changes" }).ClickAsync();

        var announced = session.Page.GetByText("Applied as revision").First;
        await announced.WaitForAsync();
        return (await announced.InnerTextAsync()).Split(' ')[^1];
    }
}
