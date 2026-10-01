using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A person is created, changed and disabled under the pattern language: an editor, a token kept in the dialog with
/// a Copy button, a confirm before access is revoked (inventory defects #6, #8, #10).
/// </summary>
/// <remarks>Its own world: it creates people whose addresses another class also uses.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class PersonEditorScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_create_creates_one_person()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        await session.Page.GetByTestId("person-new").ClickAsync();
        await session.Dialog("person-create").WaitForAsync();
        (await session.FocusedAsync()).ShouldStartWith("input#new-person-email");
        await session.Page.Keyboard.TypeAsync("twice@example.com");
        await session.Dialog("person-create").GetByTestId("person-create-run").DblClickAsync();

        await session.SnackbarAsync("Created twice@example.com");

        /* The row, not the snackbar, is what says the list was read again; and a second submit that slipped past
           the gate would be refused as a duplicate address, so it would show as a panel, not as a second row. */
        var arrived = session.Content.Locator("[data-alvo-new]").Filter(new() { HasText = "twice@example.com" });
        await arrived.WaitForAsync();
        (await session.Content.GetByText("twice@example.com").CountAsync()).ShouldBe(1);
        (await arrived.CountAsync()).ShouldBe(1, "a created person is highlighted as well as shown");
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "the second click was not a second create");
        await session.WaitForInViewAsync(arrived);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_empty_address_is_refused_under_the_field_with_focus()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        await session.Page.GetByTestId("person-new").ClickAsync();
        var editor = session.Dialog("person-create");
        await editor.WaitForAsync();
        await session.Page.Keyboard.PressAsync("Enter");

        await AssertFieldRefusedAsync(session, "new-person-email", "Type the address");
        (await editor.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "a field's refusal is under the field");
        (await session.SnackbarCountAsync()).ShouldBe(0, "an error is never a snackbar");
        (await editor.CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_editor_takes_focus_on_open_and_Escape_hands_it_back_to_the_row()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var person = await CreatePersonAsync(session, "focus@example.com");

        await session.OpenPersonAsync(person);
        await session.WaitForFocusInsideAsync("person-editor");
        await session.Page.Keyboard.PressAsync("Escape");

        await session.Dialog("person-editor").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.WaitForFocusOnAsync($"change-{person}");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_mistyped_tenant_is_refused_in_the_editor_with_focus_and_nothing_is_written()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var person = await CreatePersonAsync(session, "tenant@example.com");

        await session.OpenPersonAsync(person);
        await session.Page.FillAsync($"#tenant-{person}", "not-a-uuid");
        await session.Page.Keyboard.PressAsync("Enter");

        var editor = session.Dialog("person-editor");
        await AssertFieldRefusedAsync(session, $"tenant-{person}", "uuid");
        (await editor.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "a field's refusal is under the field");
        (await session.SnackbarCountAsync("tenant id")).ShouldBe(0, "an error is never a snackbar");
        (await Row(session, person).InnerTextAsync()).ShouldContain("no tenant");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_token_stays_in_the_editor_until_it_is_closed_and_Copy_link_puts_its_link_on_the_clipboard()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var person = await CreatePersonAsync(session, "token@example.com");

        await session.OpenPersonAsync(person);
        await session.Dialog("person-editor").GetByTestId("person-issue-token").ClickAsync();
        var link = session.Dialog("person-editor").GetByTestId("person-token-link");
        await link.WaitForAsync();
        await session.Dialog("person-editor").GetByTestId("person-token-copy").ClickAsync();

        await session.SnackbarAsync("Link copied");
        var copied = await session.Page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        (await link.InnerTextAsync()).Trim().ShouldBe(copied);
        copied.ShouldContain($"{AlvoAdmin.SetPasswordPath}#email=");
        (await session.Content.GetByText("Credential token for").CountAsync()).ShouldBe(0, "no panel at the top of the page");
    }

    /// <summary>
    /// A token is shown once, so leaving the editor by Escape, Save or Cancel before it was copied asks first, and
    /// Keep editing keeps it on screen; once copied, the editor closes without a question (final review M14).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Leaving_the_editor_before_the_token_is_copied_asks_first()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var person = await CreatePersonAsync(session, "uncopied@example.com");
        await session.OpenPersonAsync(person);
        var editor = session.Dialog("person-editor");
        await editor.GetByTestId("person-issue-token").ClickAsync();
        await editor.GetByTestId("person-token").WaitForAsync();

        await session.Page.Keyboard.PressAsync("Escape");
        await KeepTheTokenAsync(editor);
        await editor.GetByTestId("person-save").ClickAsync();
        await KeepTheTokenAsync(editor);

        await editor.GetByTestId("editor-cancel").ClickAsync();
        await editor.GetByTestId("editor-discard-question").WaitForAsync();
        (await editor.GetByTestId("editor-discard").InnerTextAsync()).Trim().ShouldBe("Leave without copying");
        await editor.GetByTestId("editor-discard").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.OpenPersonAsync(person);
        await editor.GetByTestId("person-issue-token").ClickAsync();
        await editor.GetByTestId("person-token-copy").ClickAsync();
        await session.SnackbarAsync("Link copied");
        await editor.GetByTestId("editor-cancel").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }

    /// <summary>Answers the question about the uncopied token with Keep editing, and checks the token is still there.</summary>
    private static async Task KeepTheTokenAsync(ILocator editor)
    {
        var question = editor.GetByTestId("editor-discard-question");
        await question.WaitForAsync();
        (await question.InnerTextAsync()).ShouldContain("Leave without copying the link?");
        (await question.InnerTextAsync()).ShouldContain("It is shown only once.");
        await editor.GetByTestId("editor-keep").ClickAsync();
        await editor.GetByTestId("person-token").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Disabling_a_person_closes_the_editor_and_needs_a_confirm()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var person = await CreatePersonAsync(session, "leaver@example.com");

        await session.OpenPersonAsync(person);
        await session.Dialog("person-editor").GetByTestId("person-disable").ClickAsync();
        var confirm = session.Dialog("disable-person");
        await confirm.WaitForAsync();
        (await session.Dialog("person-editor").CountAsync()).ShouldBe(0, "never a dialog over a dialog");

        await confirm.GetByTestId("disable-person-cancel").ClickAsync();
        (await Row(session, person).InnerTextAsync()).ShouldNotContain("disabled");
        await WaitForFocusOnRowAsync(session, person);

        await session.OpenPersonAsync(person);
        await session.Dialog("person-editor").GetByTestId("person-disable").ClickAsync();
        await session.Dialog("disable-person").GetByTestId("disable-person-run").ClickAsync();
        await session.SnackbarAsync("Disabled leaver@example.com");
        await Row(session, person).Filter(new() { HasText = "disabled" }).WaitForAsync();
        await WaitForFocusOnRowAsync(session, person);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Roles_change_on_save_and_an_unsaved_change_is_guarded()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var person = await CreatePersonAsync(session, "roles@example.com");

        await session.OpenPersonAsync(person);
        await session.Dialog("person-editor").GetByRole(AriaRole.Button, new() { Name = "dispatcher", Exact = true }).ClickAsync();
        await session.Page.Keyboard.PressAsync("Escape");
        await session.Dialog("person-editor").GetByTestId("editor-discard-question").WaitForAsync();
        await session.Dialog("person-editor").GetByTestId("editor-keep").ClickAsync();

        await session.Dialog("person-editor").GetByTestId("person-save").ClickAsync();

        await session.SnackbarAsync("Saved roles@example.com");
        await Row(session, person).Filter(new() { HasText = "dispatcher" }).WaitForAsync();
    }

    private static ILocator Row(AdminSession session, string id) => session.Page.Locator($"#person-{id}");

    /// <summary>
    /// Waits for a field refused in place (spec §3.3): marked invalid, described by the sentence that says why, and
    /// holding focus.
    /// </summary>
    /// <param name="session">The signed-in session.</param>
    /// <param name="id">The input's id.</param>
    /// <param name="because">Part of the sentence under it.</param>
    internal static async Task AssertFieldRefusedAsync(AdminSession session, string id, string because)
    {
        await session.Page.Locator($"#{id}[aria-invalid='true']").WaitForAsync();
        await session.WaitForFocusOnAsync(id);
        var description = await session.Page.EvaluateAsync<string>(
            "id => (document.getElementById(id).getAttribute('aria-describedby') ?? '').split(' ')"
            + ".map(part => document.getElementById(part)?.textContent ?? '').join(' ')", id);
        description.ShouldContain(because);
    }

    /// <summary>Waits for focus to be on a person's row or inside it: where a confirm that closed hands it back.</summary>
    private static async Task WaitForFocusOnRowAsync(AdminSession session, string id)
        => await session.Page.WaitForFunctionAsync(
            "id => { const row = document.getElementById(id); const at = document.activeElement;"
            + " return !!row && !!at && at !== document.body && (at.contains(row) || row.contains(at)); }",
            $"person-{id}");

    /// <summary>Opens Access and creates a person there; answers their id.</summary>
    private static async Task<string> CreatePersonAsync(AdminSession session, string email)
    {
        await session.GoAsync("/access");
        return await session.CreatePersonAsync(email);
    }
}
