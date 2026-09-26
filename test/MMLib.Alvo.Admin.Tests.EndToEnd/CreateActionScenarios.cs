using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Every create is found in one place per level and opens the same way (spec §3.1, §3.7; final review M1): a screen's
/// create is its page header's primary action, a tab's create sits in its section head, never under a list; the
/// trigger says "New …", the editor is titled the same, and its submit says what its kind of edit does.
/// </summary>
/// <remarks>
/// It opens each editor and closes it with nothing typed, so it changes nothing and shares the world with no one.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class CreateActionScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>A screen's create: the page header's primary action.</summary>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("/schema", "New entity", "new-entity", "Add to the working copy")]
    [InlineData("/data/regions", "New record", "record-sheet", "Create record")]
    [InlineData("/access", "New person", "person-create", "Create person")]
    public async Task A_screens_create_is_its_headers_primary_action(string path, string trigger, string editor, string submit)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync(path);

        var button = session.Button(trigger, exact: true);
        (await button.EvaluateAsync<bool>(
                "b => !!b.closest('header') && !b.closest(\"[data-testid='pagehead-secondary']\")"))
            .ShouldBeTrue($"{trigger} is the page header's primary action");
        await OpensAsync(session, button, editor, trigger, submit);
    }

    /// <summary>A tab's create: in its section head, above the list it adds to.</summary>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("Fields", "New field", "field-sheet", "field-row-reference")]
    [InlineData("Indexes", "New index", "index-editor", "index-row")]
    [InlineData("On write", "New hook", "hook-editor", null)]
    public async Task A_tabs_create_sits_in_its_section_head_above_the_list(
        string tab, string trigger, string editor, string? row)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync(tab);

        var button = session.Button(trigger, exact: true);
        var panel = session.Page.GetByTestId("entity-tabpanel");
        if (row is not null)
        {
            (await button.EvaluateAsync<bool>(
                    "(b, row) => !!(b.compareDocumentPosition(document.querySelector(`[data-testid='${row}']`)) & Node.DOCUMENT_POSITION_FOLLOWING)",
                    row))
                .ShouldBeTrue($"{trigger} sits above the list, never under it");
        }

        var head = await AdminSession.SettledBoxAsync(button);
        var frame = await AdminSession.SettledBoxAsync(panel);
        (frame.X + frame.Width - (head.X + head.Width)).ShouldBeLessThan(40, $"{trigger} is right-aligned in its head");
        await OpensAsync(session, button, editor, trigger, "Add to the working copy");
    }

    /// <summary>An edit's submit says what its kind of edit does: "Save changes" at once, the working copy when staged.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_edit_saves_changes_or_saves_to_the_working_copy()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("edit-field-reference").ClickAsync();
        await SubmitIsAsync(session, "field-sheet", "field-save", "Save to the working copy");

        await session.GoAsync("/access");
        await session.OpenPersonAsync(await session.PersonIdAsync(AdminWorld.AdminEmail));
        await SubmitIsAsync(session, "person-editor", "person-save", "Save changes");
    }

    private static async Task OpensAsync(AdminSession session, ILocator trigger, string editor, string title, string submit)
    {
        await trigger.ClickAsync();
        var dialog = session.Dialog(editor);
        await dialog.WaitForAsync();
        (await dialog.InnerTextAsync()).ShouldContain(title, Case.Sensitive, "the editor is titled as its trigger names it");
        await SubmitIsAsync(session, editor, null, submit);
    }

    private static async Task SubmitIsAsync(AdminSession session, string editor, string? submitTestId, string submit)
    {
        var dialog = session.Dialog(editor);
        var button = submitTestId is null
            ? dialog.GetByRole(AriaRole.Button, new() { Name = submit, Exact = true })
            : dialog.GetByTestId(submitTestId);
        (await button.InnerTextAsync()).Trim().ShouldBe(submit);
        await session.Page.Keyboard.PressAsync("Escape");
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }
}
