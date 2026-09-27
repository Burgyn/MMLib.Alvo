using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A record created off the grid's page is revealed: the grid narrows to it, says so with Clear, and selects and
/// lights it (spec §3.5, amended 27 Sep; docs/todo-admin.md §8d item 44).
/// </summary>
/// <remarks>Its own world, so <c>regions</c> holds exactly the full page this class writes and the one it creates.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RecordRevealScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_record_that_sorts_onto_the_next_page_is_revealed_selected_and_lit_and_Clear_shows_the_page_again()
    {
        await RecordEditorScenarios.SeedRegionsAsync(world, "RV", 25);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/regions");
        var rows = session.Page.GetByTestId("grid-row");
        await rows.Nth(24).WaitForAsync();
        await session.Page.GetByRole(AriaRole.Columnheader, new() { Name = "Code" }).GetByRole(AriaRole.Button).ClickAsync();
        await session.Page.GetByRole(AriaRole.Columnheader, new() { Name = "Code" })
            .And(session.Page.Locator("[aria-sort='ascending']")).WaitForAsync();

        await session.Button("New record", exact: true).ClickAsync();
        var editor = session.Dialog("record-sheet");
        await session.WaitForFocusOnAsync("rf-code");
        await session.Page.FillAsync("#rf-code", "ZZ-REVEALED");
        await session.Page.FillAsync("#rf-name", "Sorts last");
        await editor.GetByTestId("record-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        var revealing = session.Page.GetByTestId("grid-revealing");
        await revealing.WaitForAsync();
        (await revealing.InnerTextAsync()).ShouldContain("Showing the record you created.");
        var created = rows.Filter(new() { HasText = "ZZ-REVEALED" });
        await created.And(session.Page.Locator("[aria-selected='true']")).WaitForAsync();
        (await created.GetAttributeAsync("data-alvo-new")).ShouldBe("true", "lit as every created item is");
        (await rows.CountAsync()).ShouldBe(1, "narrowed to the record by its id");

        await session.Page.GetByTestId("grid-reveal-clear").ClickAsync();
        await revealing.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await rows.Nth(24).WaitForAsync();
        (await created.CountAsync()).ShouldBe(0, "back on the first page, where ZZ does not sort");
        await session.Page.GetByTestId("grid-next").WaitForAsync();
        await session.WaitForFocusOnTestIdAsync("grid-search");
        session.AssertConsoleClean();
    }
}
