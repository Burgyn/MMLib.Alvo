using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A message template is declared and edited on Integrations — subject and body only — and an email hook offers it before
/// it is applied (spec §4.8, §6.2). Each scenario uses its own names: the class shares one working copy.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class TemplateDeclarationScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_template_with_a_broken_subject_or_a_tenant_placeholder_is_refused_at_its_fields()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        var editor = await NewTemplateAsync(session);

        await session.Page.FillAsync("#template-name", "broken-template");
        await session.Page.FillAsync("#template-subject", "Ready\u2028now");
        await session.Page.FillAsync("#template-body", "Hi {{@tenant.id}}");
        await editor.GetByTestId("template-save").ClickAsync();

        await session.WaitForFocusOnAsync("template-subject");
        (await session.Page.Locator("#template-subject-problem").InnerTextAsync()).ShouldContain("one line");
        (await session.Page.Locator("#template-body-problem").InnerTextAsync()).ShouldContain("carries no tenant");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_template_is_offered_to_an_email_hook_before_it_is_applied()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        var editor = await NewTemplateAsync(session);
        await session.Page.FillAsync("#template-name", "pickup-reminder");
        await session.Page.FillAsync("#template-subject", "  Your bike {{new.order_number}} is waiting  ");
        await session.Page.FillAsync("#template-body", "Please collect it.");
        await editor.GetByTestId("template-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.SnackbarAsync("Template pickup-reminder added to the working copy");
        var row = session.Page.Locator("#template-pickup-reminder");
        (await row.GetAttributeAsync("data-alvo-new")).ShouldBe("true");
        (await row.InnerTextAsync()).ShouldContain("Your bike {{new.order_number}} is waiting");

        await HookPickerScenarios.NewAfterHookAsync(session, "service_orders", "afterUpdate", "email");
        await HookPickerScenarios.Combobox(session, "Template").ClickAsync();
        var offered = session.Page.GetByRole(AriaRole.Option, new() { Name = "pickup-reminder (not applied yet)", Exact = true });
        await offered.WaitForAsync();
        (await offered.CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_template_is_edited_with_its_name_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.Locator("#template-order-ready [data-testid='template-edit']").ClickAsync();
        await session.WaitForFocusInsideAsync("template-editor", FocusScope.Dialog);
        var editor = session.Dialog("template-editor");

        (await editor.InnerTextAsync()).ShouldContain("Edit template order-ready");
        (await editor.GetByTestId("template-name-fixed").InnerTextAsync()).ShouldBe("order-ready");
        (await editor.GetByTestId("template-statement-build").InnerTextAsync()).ShouldContain("bodyFile");
        (await session.Page.InputValueAsync("#template-body")).ShouldStartWith("Hello,");
        await session.Page.FillAsync("#template-subject", "Your bike is ready: order {{new.order_number}}");
        await editor.GetByTestId("template-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.SnackbarAsync("Template order-ready saved to the working copy");
        var row = session.Page.Locator("#template-order-ready");
        (await row.GetAttributeAsync("data-alvo-new")).ShouldBe("true");
        (await row.InnerTextAsync()).ShouldContain("not applied yet");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Escape_on_a_typed_template_asks_before_it_discards()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        var editor = await NewTemplateAsync(session);
        await session.Page.FillAsync("#template-name", "unsaved-template");

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.GetByTestId("editor-discard-question").WaitForAsync();
        await editor.GetByTestId("editor-keep").ClickAsync();
        /* On the box itself: focus never left the dialog for the question, so a wait for focus inside it passes at once. */
        await session.WaitForFocusOnAsync("template-name");
        (await session.Page.InputValueAsync("#template-name")).ShouldBe("unsaved-template");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_template_sheet_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/integrations");
        await NewTemplateAsync(session);

        await session.AssertNoHorizontalScrollAsync();
    }

    /// <summary>Opens the new template sheet and waits until it has taken focus.</summary>
    /// <remarks>
    /// The sheet takes focus a render after it shows (<see cref="AdminSession.WaitForFocusInsideAsync"/>): typed into
    /// before then, a box loses focus to the sheet's first control, and an Escape goes to the page and closes nothing.
    /// </remarks>
    /// <param name="session">The signed-in session, on Integrations.</param>
    /// <returns>The sheet.</returns>
    private static async Task<ILocator> NewTemplateAsync(AdminSession session)
    {
        await session.Page.GetByTestId("template-new").ClickAsync();
        await session.WaitForFocusInsideAsync("template-editor", FocusScope.Dialog);
        return session.Dialog("template-editor");
    }
}
