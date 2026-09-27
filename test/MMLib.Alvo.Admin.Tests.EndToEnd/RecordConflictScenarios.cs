using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A save or a delete that lost to another writer is refused in place, with Reload, never a silent overwrite and
/// never a snackbar; an entity that keeps no version says once that the last write wins (docs/todo-admin.md §8d
/// item 25).
/// </summary>
/// <remarks>
/// <para>
/// <b>The other writer is the data port itself</b>, called through the running host's services as the tenant's own
/// system caller — a second tab, an automation, a script over <c>/api</c> all reach the row this way. It writes while
/// the operator's editor is open, which is the one moment the version the editor holds goes stale.
/// </para>
/// <para>Its own world: it writes rows whose keys another class also uses.</para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RecordConflictScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly TenantId _tenant = TenantId.New();

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_save_after_somebody_else_saved_is_refused_in_place_and_Reload_shows_their_change()
    {
        await RecordEditorScenarios.SeedWorkOrderAsync(world, _tenant, "WO-0301");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");
        await RecordEditorScenarios.RowEditAsync(session, "WO-0301");
        var editor = session.Dialog("record-sheet");
        (await editor.GetByTestId("record-last-write-wins").CountAsync()).ShouldBe(0, "work_orders is audited");

        await session.Page.FillAsync("#rf-title", "Mine");
        await ChangeElsewhereAsync("WO-0301", "Theirs");
        await editor.GetByTestId("record-save").ClickAsync();

        var panel = editor.GetByTestId("error-panel");
        await panel.WaitForAsync();
        (await editor.GetByTestId("error-title").InnerTextAsync()).ShouldBe("This record changed since you opened it");
        await session.WaitForFocusInsideAsync("error-panel");
        (await session.SnackbarCountAsync("Saved")).ShouldBe(0, "a refused save never says it saved");

        await editor.GetByTestId("record-reload").ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.WaitForFunctionAsync("() => document.querySelector('#rf-title')?.value === 'Theirs'");
        await session.WaitForFocusInsideAsync("record-sheet");
        await RecordEditorScenarios.Row(session, "WO-0301").Filter(new() { HasText = "Theirs" }).WaitForAsync();

        await session.Page.FillAsync("#rf-title", "Mine, after theirs");
        await editor.GetByTestId("record-save").ClickAsync();
        await session.SnackbarAsync("Saved Mine, after theirs");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_delete_after_somebody_else_saved_is_refused_and_Reload_reads_the_page_again()
    {
        await RecordEditorScenarios.SeedWorkOrderAsync(world, _tenant, "WO-0302");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");

        var confirm = await RecordEditorScenarios.AskToDeleteAsync(session, "WO-0302");
        await ChangeElsewhereAsync("WO-0302", "Kept elsewhere 0302");
        await confirm.GetByTestId("delete-record-run").ClickAsync();

        var panel = session.Content.GetByTestId("error-panel");
        await panel.WaitForAsync();
        (await panel.GetByTestId("error-title").InnerTextAsync()).ShouldBe("This record changed since you opened it");
        await session.WaitForFocusInsideAsync("error-panel");
        (await session.SnackbarCountAsync("Record deleted")).ShouldBe(0, "the delete did not happen");

        await session.Page.GetByTestId("grid-reload").ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        var row = RecordEditorScenarios.Row(session, "WO-0302");
        await row.Filter(new() { HasText = "Kept elsewhere 0302" }).WaitForAsync();
        await session.Page.WaitForFunctionAsync(
            "() => document.activeElement?.closest(\"[data-testid='grid-row']\")?.innerText.includes('WO-0302')");

        confirm = await RecordEditorScenarios.AskToDeleteAsync(session, "WO-0302");
        await confirm.GetByTestId("delete-record-run").ClickAsync();
        await session.SnackbarAsync("Record deleted");
        await row.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_that_keeps_no_version_says_once_that_the_last_write_wins()
    {
        await RecordEditorScenarios.SeedWorkOrderAsync(world, _tenant, "WO-0303");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/customers");

        await RecordEditorScenarios.RowEditAsync(session, "Customer of WO-0303");
        var editor = session.Dialog("record-sheet");
        var note = editor.GetByTestId("record-last-write-wins");
        (await note.CountAsync()).ShouldBe(1, "said once");
        (await note.InnerTextAsync()).ShouldStartWith("Last write wins here");

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Button("New record", exact: true).ClickAsync();
        await editor.WaitForAsync();
        (await note.CountAsync()).ShouldBe(0, "a create overwrites nobody");
        session.AssertConsoleClean();
    }

    /// <summary>Another writer changes the work order's title while the operator's editor is open.</summary>
    private async Task ChangeElsewhereAsync(string reference, string title)
    {
        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        var page = await data.QueryAsync(
            new AlvoQuery
            {
                Entity = "work_orders",
                Filter = new AlvoComparison("reference", AlvoFilterOperator.Eq, reference),
            },
            system);
        await data.UpdateAsync(
            "work_orders", FieldServiceSeed.IdOf(page.Items.Single()), new Dictionary<string, object?> { ["title"] = title }, system);
    }
}
