using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A record created off the grid's page is revealed: the grid narrows to it, says so with Clear, and selects and
/// lights it (spec §3.5, amended 27 Sep; docs/todo-admin.md §8d item 44).
/// </summary>
/// <remarks>
/// Its own world, so <c>regions</c> holds exactly the full page this class writes and the one it creates. The rest
/// reveal a customer created while a search excluded it: <c>customers</c> can be deleted, <c>regions</c> cannot.
/// </remarks>
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

    private static readonly TenantId _tenant = TenantId.New();

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Clear_puts_back_the_search_the_reveal_replaced()
    {
        await using var session = await RevealACustomerAsync("WO-0401", "Revealed customer 1", TestContext.Current.CancellationToken);

        await session.Page.GetByTestId("grid-reveal-clear").ClickAsync();
        await session.Page.GetByTestId("grid-revealing").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await RecordEditorScenarios.Row(session, "Customer of WO-0401").WaitForAsync();
        (await session.Page.InputValueAsync("[data-testid='grid-search']")).ShouldBe("Customer of WO-0401");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Deleting_the_revealed_record_ends_the_reveal_without_blaming_a_read_rule()
    {
        await using var session = await RevealACustomerAsync("WO-0402", "Revealed customer 2", TestContext.Current.CancellationToken);

        var confirm = await RecordEditorScenarios.AskToDeleteAsync(session, "Revealed customer 2");
        await confirm.GetByTestId("delete-record-run").ClickAsync();
        await session.SnackbarAsync("Record deleted");

        await session.Page.GetByTestId("grid-revealing").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await RecordEditorScenarios.Row(session, "Customer of WO-0402").WaitForAsync();
        (await session.Page.InputValueAsync("[data-testid='grid-search']")).ShouldBe("Customer of WO-0402");
        (await session.Page.GetByTestId("grid-reveal-lost").CountAsync()).ShouldBe(0, "the operator deleted it, and knows");
        (await session.Content.InnerTextAsync()).ShouldNotContain("read rule");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_revealed_record_another_writer_deletes_ends_the_reveal_and_says_so()
    {
        await using var session = await RevealACustomerAsync("WO-0403", "Revealed customer 3", TestContext.Current.CancellationToken);
        await RecordConflictScenarios.ElsewhereAsync(world, _tenant, "customers", "name", "Revealed customer 3",
            (data, id, system) => data.DeleteAsync("customers", id, system));

        await session.Page.GetByRole(AriaRole.Columnheader, new() { Name = "Name" }).GetByRole(AriaRole.Button).ClickAsync();

        var lost = session.Page.GetByTestId("grid-reveal-lost");
        await lost.WaitForAsync();
        (await lost.InnerTextAsync()).ShouldContain("it was deleted, or the read rule no longer admits it");
        (await session.Page.GetByTestId("grid-revealing").CountAsync()).ShouldBe(0);
        await RecordEditorScenarios.Row(session, "Customer of WO-0403").WaitForAsync();
        (await session.Page.InputValueAsync("[data-testid='grid-search']")).ShouldBe("Customer of WO-0403");
        (await session.Content.InnerTextAsync()).ShouldNotContain("The Rules screen shows the predicate");
        session.AssertConsoleClean();
    }

    /// <summary>Searches the customers for one that is not the one it then creates, which is therefore revealed.</summary>
    private async Task<AdminSession> RevealACustomerAsync(string reference, string name, CancellationToken cancel)
    {
        await RecordEditorScenarios.SeedWorkOrderAsync(world, _tenant, reference);
        var session = await world.SignInAsync(cancel);
        await session.GoAsync("/data/customers");
        await session.Page.FillAsync("[data-testid='grid-search']", $"Customer of {reference}");
        await session.Page.WaitForFunctionAsync(
            "() => document.querySelectorAll(\"[data-testid='grid-row']\").length === 1");
        await RecordEditorScenarios.Row(session, $"Customer of {reference}").WaitForAsync();

        await session.Button("New record", exact: true).ClickAsync();
        var editor = session.Dialog("record-sheet");
        await session.WaitForFocusOnAsync("rf-name");
        await session.Page.FillAsync("#rf-name", name);
        await editor.GetByRole(AriaRole.Radio, new() { Name = "standard", Exact = true }).ClickAsync();
        await editor.GetByTestId("record-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.Page.GetByTestId("grid-revealing").WaitForAsync();
        await RecordEditorScenarios.Row(session, name).And(session.Page.Locator("[aria-selected='true']")).WaitForAsync();
        (await session.Page.InputValueAsync("[data-testid='grid-search']")).ShouldBe(string.Empty);
        return session;
    }
}
