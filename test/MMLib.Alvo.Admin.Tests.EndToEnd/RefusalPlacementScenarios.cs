namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Every refusal the real host publishes reaches a screen, and each "not yet" page finds its own warned block
/// (docs/todo-admin.md §8d items 19 and 20). Read-only: nothing is staged.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class RefusalPlacementScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>The drift guard: a slot the core adds and <c>RefusalPlaces</c> does not place fails here.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task No_refusal_the_build_publishes_is_left_without_a_screen()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.GetByTestId("overview-links").WaitForAsync();
        (await session.Page.GetByTestId("overview-unplaced").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }

    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("/automations")]
    [InlineData("/functions")]
    public async Task A_not_yet_page_finds_its_warned_block_and_shows_the_wildcard_refusal(string route)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync(route);

        await session.Page.GetByTestId("not-yet-warned").WaitForAsync();
        (await session.Page.GetByTestId("not-yet-unknown").CountAsync()).ShouldBe(0);
        await session.Page.GetByTestId("refused-trigger.event").WaitForAsync();

        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Integrations_says_why_a_body_file_and_a_jsonata_payload_are_refused()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");

        await session.Page.GetByTestId("integrations-refused-bodyFile").WaitForAsync();
        await session.Page.GetByTestId("integrations-refused-JSONata").WaitForAsync();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The field editor's refused-facets fold lists the rollup filter beside <c>field.*</c>.
    /// </summary>
    /// <remarks>
    /// Counted rather than waited on: the fold is shut by default, and what is asserted is that the refusal is in it,
    /// not that it is open. The sheet is found as the dialog it is (<see cref="AdminSession.Dialog"/>).
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_field_editor_lists_the_rollup_filter_among_its_refusals()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Dialog("field-sheet");
        await sheet.GetByTestId("refused-facets").WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        (await sheet.GetByTestId("refused-rollup.where").CountAsync()).ShouldBe(1);

        session.AssertConsoleClean();
    }
}
