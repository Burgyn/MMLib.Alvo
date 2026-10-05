using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Integrations lists the working copy's endpoints and templates and declares endpoints behind the build's statement (spec
/// §4.7, §4.8, §6; rulings B5, B6). Each scenario uses its own names: the class shares one working copy.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class IntegrationsScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_screen_lists_the_working_copys_endpoints_and_templates_with_who_uses_them()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");

        var desk = await session.Page.Locator("#endpoint-rental-desk").InnerTextAsync();
        desk.ShouldContain("posted to by rentals afterCreate");
        desk.ShouldContain("not signed");
        desk.ShouldContain("rental-desk-signing-key");
        (await session.Page.Locator("#template-order-ready").InnerTextAsync()).ShouldContain("sent by service_orders afterUpdate");
        await session.Page.GetByTestId("integrations-refused-bodyFile").WaitForAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_endpoint_is_declared_under_the_statement_and_lit_in_the_list()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        var editor = await NewEndpointAsync(session);

        var statement = await editor.GetByTestId("endpoint-statement").InnerTextAsync();
        statement.ShouldContain("Deliveries to this endpoint are not signed, and carry the whole row");
        statement.ShouldContain("WebhookAllowedNetworks");
        (await editor.GetByTestId("endpoint-statement-build").InnerTextAsync()).ShouldContain("no delivery is signed");

        await session.Page.FillAsync("#endpoint-name", "billing-system");
        /* Waited on, not read: the suggested secret name comes back from the circuit after the fill, and a browser-local
           read taken at once sees the box before that render (it failed so in a full-suite run). */
        await session.Page.WaitForFunctionAsync("() => document.querySelector('#endpoint-secret')?.value === 'billing-system-signing-key'");
        await session.Page.FillAsync("#endpoint-url", "https://billing.example/hooks/alvo");
        await editor.GetByTestId("endpoint-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.SnackbarAsync("Endpoint billing-system added to the working copy");
        var row = session.Page.Locator("#endpoint-billing-system");
        (await row.GetAttributeAsync("data-alvo-new")).ShouldBe("true");
        var text = await row.InnerTextAsync();
        text.ShouldContain("not applied yet");
        text.ShouldContain("no hook posts here");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_endpoint_the_build_would_refuse_is_refused_at_its_fields_without_echoing_a_secret()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        var editor = await NewEndpointAsync(session);

        await session.Page.FillAsync("#endpoint-name", "Billing");
        await session.Page.FillAsync("#endpoint-url", "http://example.com/hooks");
        await session.Page.FillAsync("#endpoint-secret", "Sup3r+Secret==");
        await editor.GetByTestId("endpoint-save").ClickAsync();

        await session.WaitForFocusOnAsync("endpoint-name");
        (await session.Page.Locator("#endpoint-name-problem").InnerTextAsync()).ShouldContain("not an endpoint name");
        (await session.Page.Locator("#endpoint-url-problem").InnerTextAsync()).ShouldContain("https");
        (await session.Page.Locator("#endpoint-secret-problem").InnerTextAsync()).ShouldNotContain("Sup3r");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_loopback_http_url_is_accepted_as_the_build_accepts_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        var editor = await NewEndpointAsync(session);

        await session.Page.FillAsync("#endpoint-name", "local-desk");
        await session.Page.FillAsync("#endpoint-url", "http://localhost:5099/hook");
        await editor.GetByTestId("endpoint-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.Page.Locator("#endpoint-local-desk").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_endpoint_is_edited_with_its_name_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.Locator("#endpoint-rental-desk [data-testid='endpoint-edit']").ClickAsync();
        var editor = session.Dialog("endpoint-editor");
        await session.WaitForFocusInsideAsync("endpoint-editor", FocusScope.Dialog);

        (await editor.InnerTextAsync()).ShouldContain("Edit endpoint rental-desk");
        (await editor.GetByTestId("endpoint-name-fixed").InnerTextAsync()).ShouldBe("rental-desk");
        await session.Page.FillAsync("#endpoint-description", "The rental counter's receiver");
        await editor.GetByTestId("endpoint-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.SnackbarAsync("Endpoint rental-desk saved to the working copy");
        (await session.Page.Locator("#endpoint-rental-desk").InnerTextAsync()).ShouldContain("not applied yet");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_endpoint_is_offered_to_a_webhook_hook_before_it_is_applied()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        var editor = await NewEndpointAsync(session);
        await session.Page.FillAsync("#endpoint-name", "dispatch-desk");
        await session.Page.FillAsync("#endpoint-url", "https://dispatch.example/hooks");
        await editor.GetByTestId("endpoint-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await HookPickerScenarios.NewAfterHookAsync(session, "rentals", "afterUpdate", "webhook");
        await HookPickerScenarios.Combobox(session, "Endpoint").ClickAsync();
        var offered = session.Page.GetByRole(AriaRole.Option, new() { Name = "dispatch-desk (not applied yet)", Exact = true });
        await offered.WaitForAsync();
        (await offered.CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_Add_declares_one_endpoint()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        var editor = await NewEndpointAsync(session);
        await session.Page.FillAsync("#endpoint-name", "twice-desk");
        await session.Page.FillAsync("#endpoint-url", "https://twice.example/hooks");

        await editor.GetByTestId("endpoint-save").DblClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#endpoint-twice-desk").CountAsync()).ShouldBe(1);
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Escape_on_a_typed_endpoint_asks_before_it_discards()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        var editor = await NewEndpointAsync(session);
        await session.Page.FillAsync("#endpoint-name", "unsaved-desk");

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.GetByTestId("editor-discard-question").WaitForAsync();
        await editor.GetByTestId("editor-keep").ClickAsync();
        await session.WaitForFocusInsideAsync("endpoint-editor", FocusScope.Dialog);
        (await session.Page.InputValueAsync("#endpoint-name")).ShouldBe("unsaved-desk");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_endpoint_sheet_and_the_list_fit_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/integrations");
        await session.AssertNoHorizontalScrollAsync();

        await NewEndpointAsync(session);
        await session.AssertNoHorizontalScrollAsync();
    }

    /// <summary>Opens the new endpoint sheet and waits until it has taken focus.</summary>
    /// <remarks>
    /// The sheet takes focus a render after it shows (<see cref="AdminSession.WaitForFocusInsideAsync"/>): typed into
    /// before then, a box loses focus to the sheet's first control, and an Escape goes to the page and closes nothing.
    /// </remarks>
    /// <param name="session">The signed-in session, on Integrations.</param>
    /// <returns>The sheet.</returns>
    private static async Task<ILocator> NewEndpointAsync(AdminSession session)
    {
        await session.Page.GetByTestId("endpoint-new").ClickAsync();
        await session.WaitForFocusInsideAsync("endpoint-editor", FocusScope.Dialog);
        return session.Dialog("endpoint-editor");
    }
}
