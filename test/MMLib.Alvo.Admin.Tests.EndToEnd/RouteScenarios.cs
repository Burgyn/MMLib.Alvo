using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Every legal entity name reaches its own schema screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins</b> (docs/architecture/admin-dashboard-review.md, F-12): Preview and Transfer used
/// to be served at <c>/admin/schema/preview</c> and <c>/admin/schema/transfer</c>, beside
/// <c>/admin/schema/{EntityName}</c>. The schema admits <c>preview</c> and <c>transfer</c> as entity names, and
/// the router prefers a literal segment to a parameter, so an entity by either name could be created and never
/// opened. <c>AdminPathsTests</c> refuses the collision at the route table; this is the operator's view of it.
/// </para>
/// <para>
/// <b>Its own world</b>: the two entities are staged in this operator's working copy, which every other class
/// in the same world would see.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RouteScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_named_like_a_schema_screen_is_created_and_opened()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);

        foreach (var name in new[] { "preview", "transfer" })
        {
            await AddEntityAsync(session, name);

            session.Page.Url.ShouldEndWith($"/schema/{name}");
            await session.Page.GetByRole(AriaRole.Tablist, new() { Name = $"{name} sections" }).WaitForAsync();
        }

        /* And the two screens that gave way still answer at their own addresses. */
        await session.PreviewPendingAsync();

        await session.GoAsync("/transfer");
        (await session.Page.Locator("main").InnerTextAsync()).ShouldContain("byte for byte");

        session.AssertConsoleClean();
    }

    private static async Task AddEntityAsync(AdminSession session, string name)
    {
        await session.GoAsync("/schema");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "New entity", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", name);
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Add to the working copy" }).ClickAsync();
        await session.Page.WaitForURLAsync($"**/schema/{name}");
        await session.SettleAsync();
    }
}
