using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Data;
using System.Globalization;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A record is created, edited and deleted under the pattern language (spec §3): the editor guards its changes, a
/// save is a snackbar, and a delete cannot happen without its confirm (inventory defect #9).
/// </summary>
/// <remarks>
/// Its own world: it writes and deletes rows whose keys another class also uses. Every <c>regions</c> row this class
/// writes counts against one page of 25, which <see cref="A_created_record_is_selected_and_scrolled_into_view"/>
/// needs to hold the whole entity.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RecordEditorScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly TenantId _tenant = TenantId.New();
    private static readonly PageWaitForFunctionOptions _polling = new() { PollingInterval = 100 };

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_record_opens_on_its_first_field_and_a_double_click_creates_one()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/regions");

        await session.Button("New record", exact: true).ClickAsync();
        var editor = session.Dialog("record-sheet");
        await editor.WaitForAsync();
        await EditorScenarios.WaitForFocusOnAsync(session, "rf-code");
        (await session.FocusedAsync()).ShouldStartWith("input#rf-code");
        (await session.Page.GetAttributeAsync("#rf-code", "aria-required")).ShouldBe("true");

        await session.Page.FillAsync("#rf-code", "NORTH-1");
        await session.Page.FillAsync("#rf-name", "North one");
        await editor.GetByTestId("record-save").DblClickAsync();

        await session.SnackbarAsync("Created");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.WaitForTimeoutAsync(500);
        (await session.Page.GetByTestId("grid-row").Filter(new() { HasText = "NORTH-1" }).CountAsync()).ShouldBe(1);
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "the second click created nothing");
        session.AssertConsoleClean();
    }

    /// <summary>A created record appears in place, drawn as selected and scrolled to (spec §3.5).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_created_record_is_selected_and_scrolled_into_view()
    {
        await SeedRegionsAsync("FILL", 17);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/regions");
        await session.Page.GetByTestId("grid-row").Nth(16).WaitForAsync();

        await session.Button("New record", exact: true).ClickAsync();
        var editor = session.Dialog("record-sheet");
        await EditorScenarios.WaitForFocusOnAsync(session, "rf-code");
        await session.Page.FillAsync("#rf-code", "PLACED");
        await session.Page.FillAsync("#rf-name", "Placed in view");
        await editor.GetByTestId("record-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        var created = session.Page.GetByTestId("grid-row").Filter(new() { HasText = "PLACED" });
        await session.Page.GetByTestId("grid-row").And(session.Page.Locator("[aria-selected='true']"))
            .Filter(new() { HasText = "PLACED" }).WaitForAsync();
        await session.Page.WaitForFunctionAsync(InView, await created.ElementHandleAsync(), _polling);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_edited_record_asks_before_Escape_throws_the_edit_away()
    {
        await SeedAsync("WO-0101");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");
        await RowEditAsync(session, "WO-0101");
        await session.Page.FillAsync("#rf-title", "Changed but not saved");

        await session.Page.Keyboard.PressAsync("Escape");

        await session.Dialog("record-sheet").GetByTestId("editor-discard-question").WaitForAsync();
        await session.Dialog("record-sheet").GetByTestId("editor-discard").ClickAsync();
        await session.Dialog("record-sheet").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await Row(session, "WO-0101").InnerTextAsync()).ShouldNotContain("Changed but not saved");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_save_closes_the_editor_and_says_so_in_a_snackbar()
    {
        await SeedAsync("WO-0102");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");
        await RowEditAsync(session, "WO-0102");

        await session.Page.FillAsync("#rf-title", "Service call WO-0102 amended");
        await session.Page.Keyboard.PressAsync("Control+Enter");

        await session.SnackbarAsync("Saved Service call WO-0102 amended");
        await session.Dialog("record-sheet").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    /// <summary>
    /// The delete is an <c>AlvoConfirm</c> after the editor closed: modal, titled by the action, the consequence first,
    /// the verb on the button; Cancel, Escape and a click beside it keep the record, and only the verb deletes it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_delete_needs_its_confirm_and_Cancel_keeps_the_record()
    {
        await SeedAsync("WO-0103");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");

        var confirm = await AskToDeleteAsync(session, "WO-0103");
        (await session.Dialog("record-sheet").CountAsync()).ShouldBe(0, "never a dialog over a dialog");
        (await confirm.GetAttributeAsync("aria-modal")).ShouldBe("true");
        (await confirm.InnerTextAsync()).ShouldStartWith("Delete Service call WO-0103?");
        (await confirm.GetByRole(AriaRole.Paragraph).First.InnerTextAsync())
            .ShouldStartWith("The record is deleted from work_orders");
        (await confirm.GetByTestId("delete-record-run").InnerTextAsync()).Trim().ShouldBe("Delete record");
        await confirm.GetByTestId("delete-record-cancel").ClickAsync();
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await Row(session, "WO-0103").WaitForAsync();

        confirm = await AskToDeleteAsync(session, "WO-0103");
        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        confirm = await AskToDeleteAsync(session, "WO-0103");
        await session.Page.Mouse.ClickAsync(5, 5);
        await session.Page.WaitForTimeoutAsync(300);
        (await confirm.CountAsync()).ShouldBe(1, "a click beside the confirm does not dismiss it");
        await confirm.GetByTestId("delete-record-run").ClickAsync();

        await session.SnackbarAsync("Record deleted");
        await Row(session, "WO-0103").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_the_verb_deletes_once()
    {
        await SeedAsync("WO-0104");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");

        var confirm = await AskToDeleteAsync(session, "WO-0104");
        await confirm.GetByTestId("delete-record-run").DblClickAsync();

        await session.SnackbarAsync("Record deleted");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.WaitForTimeoutAsync(500);
        (await session.SnackbarCountAsync("Record deleted")).ShouldBe(1, "a double click deleted once");
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "no second delete was refused");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// A delete asked from an editor holding unsaved changes asks about them first, and Keep editing keeps them: the
    /// editor must close before the confirm opens, and closing it silently would lose the edit.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_delete_from_an_edited_record_asks_before_it_throws_the_edit_away()
    {
        await SeedAsync("WO-0105");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");
        await RowEditAsync(session, "WO-0105");
        var editor = session.Dialog("record-sheet");
        await session.Page.FillAsync("#rf-title", "Edited before the delete");

        await editor.GetByTestId("record-delete").ClickAsync();
        await editor.GetByTestId("editor-discard-question").WaitForAsync();
        (await session.Dialog("delete-record").CountAsync()).ShouldBe(0, "the question comes before the confirm");
        await editor.GetByTestId("editor-keep").ClickAsync();
        (await session.Page.InputValueAsync("#rf-title")).ShouldBe("Edited before the delete");

        await editor.GetByTestId("record-delete").ClickAsync();
        await editor.GetByTestId("editor-discard").ClickAsync();
        var confirm = session.Dialog("delete-record");
        await confirm.WaitForAsync();
        await confirm.GetByTestId("delete-record-cancel").ClickAsync();
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await Row(session, "WO-0105").InnerTextAsync()).ShouldNotContain("Edited before the delete");
        session.AssertConsoleClean();
    }

    /// <summary>Every refused save moves focus to the alert again, not only the first (spec §3.3).</summary>
    /// <remarks>Refused by the engine for a code another region holds, since <c>code</c> is unique.</remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_second_refused_save_takes_focus_again()
    {
        await SeedRegionsAsync("TAKEN", 1);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/regions");
        await session.Button("New record", exact: true).ClickAsync();
        var editor = session.Dialog("record-sheet");
        await EditorScenarios.WaitForFocusOnAsync(session, "rf-code");
        await session.Page.FillAsync("#rf-code", "TAKEN-00");
        await session.Page.FillAsync("#rf-name", "Refused");

        await editor.GetByTestId("record-save").ClickAsync();
        await EditorScenarios.WaitForFocusInsideAsync(session, "error-panel");

        await session.Page.FocusAsync("#rf-name");
        await EditorScenarios.WaitForFocusOnAsync(session, "rf-name");
        await editor.GetByTestId("record-save").ClickAsync();

        await EditorScenarios.WaitForFocusInsideAsync(session, "error-panel");
        (await editor.GetByTestId("error-panel").CountAsync()).ShouldBe(1);
        (await session.SnackbarCountAsync()).ShouldBe(0, "an error is never a snackbar");
        session.AssertConsoleClean();
    }

    /// <summary>Whether the row is inside the visible part of every box that scrolls it, and of the viewport.</summary>
    private const string InView = """
        row => {
          const box = row.getBoundingClientRect();
          const inside = r => box.top >= r.top && box.bottom <= r.bottom;
          if (!inside({ top: 0, bottom: window.innerHeight })) return false;
          for (let e = row.parentElement; e; e = e.parentElement) {
            const s = getComputedStyle(e);
            if (/(auto|scroll|hidden)/.test(s.overflowY) && e.scrollHeight > e.clientHeight && !inside(e.getBoundingClientRect())) return false;
          }
          return true;
        }
        """;

    private static ILocator Row(AdminSession session, string reference)
        => session.Page.GetByTestId("grid-row").Filter(new() { HasText = reference });

    private static async Task RowEditAsync(AdminSession session, string reference)
    {
        await Row(session, reference).GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        await session.Dialog("record-sheet").WaitForAsync();
    }

    private static async Task<ILocator> AskToDeleteAsync(AdminSession session, string reference)
    {
        await RowEditAsync(session, reference);
        await session.Dialog("record-sheet").GetByTestId("record-delete").ClickAsync();
        var confirm = session.Dialog("delete-record");
        await confirm.WaitForAsync();
        return confirm;
    }

    private async Task SeedAsync(string reference)
    {
        using var scope = world.Services.CreateScope();
        await FieldServiceSeed.GrantTheOperatorAsync(scope.ServiceProvider, _tenant);
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        var region = await FieldServiceSeed.RegionAsync(data, system, $"R-{reference}");
        var customer = await FieldServiceSeed.CustomerAsync(data, system, _tenant, $"Customer of {reference}");
        await FieldServiceSeed.WorkOrderAsync(data, system, _tenant, reference, customer, region);
    }

    private async Task SeedRegionsAsync(string prefix, int count)
    {
        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        for (var index = 0; index < count; index++)
        {
            await FieldServiceSeed.RegionAsync(
                data, AlvoContext.System(_tenant), $"{prefix}-{index.ToString("00", CultureInfo.InvariantCulture)}");
        }
    }
}
