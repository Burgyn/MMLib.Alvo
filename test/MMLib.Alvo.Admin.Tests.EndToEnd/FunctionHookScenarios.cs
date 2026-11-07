using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// An after-hook calls a host function: the hooks editor offers <c>function</c>, picks the registered one and draws a form
/// for its input; applied, a write runs it as its own delivery, which Operations lists (HX-15).
/// </summary>
/// <param name="world">The field-service world with <c>issue-invoice</c> registered.</param>
public sealed class FunctionHookScenarios(FunctionHookWorld world) : IClassFixture<FunctionHookWorld>
{
    private const string HookPrefix = "/entities/regions/hooks/afterCreate#";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_function_hook_added_through_its_input_form_runs_as_a_delivery_after_a_write()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await world.SignInAsync(ct);
        await HookPickerScenarios.NewAfterHookAsync(session, "regions", "afterCreate", "function");

        await session.ChooseAsync(HookPickerScenarios.Combobox(session, "Function"), FunctionHookWorld.IssueInvoice);
        var code = session.Page.GetByTestId("hook-input-row").Filter(new() { HasText = "code" });
        await code.GetByTestId("hook-input-0-mode").GetByRole(AriaRole.Radio, new() { Name = "an expression", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#hook-input-0", "new.code");
        await HostFunctionScenarios.AddHookAsync(session);
        (await session.Page.Locator("#hook-afterCreate-0").InnerTextAsync()).ShouldContain("\"name\": \"issue-invoice\"");
        await HostFunctionScenarios.ApplyAsync(session, "Invoice every new region from its hook");

        await world.Services.GetRequiredService<IAlvoData>().CreateAsync(
            "regions", new Dictionary<string, object?> { ["code"] = "FN-1", ["name"] = "Function north" }, AlvoContext.System(null), cancellationToken: ct);
        await session.GoAsync($"/operations?subscriber={Uri.EscapeDataString(HookPrefix)}");

        await session.Page.GetByTestId("ops-subscriber-filter").WaitForAsync();
        await WaitForSucceededAsync(session);
        world.Calls.ShouldContain("FN-1");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_required_input_left_empty_is_refused_in_the_sheet()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookPickerScenarios.NewAfterHookAsync(session, "regions", "afterUpdate", "function");
        await session.ChooseAsync(HookPickerScenarios.Combobox(session, "Function"), FunctionHookWorld.IssueInvoice);

        await session.Dialog("hook-editor").GetByTestId("hook-add").ClickAsync();

        await session.Dialog("hook-editor").GetByText("'code' is required by issue-invoice.").WaitForAsync();
    }

    private static async Task WaitForSucceededAsync(AdminSession session)
    {
        var row = session.Page.GetByTestId("ops-delivery-list").Locator("[data-testid^='ops-row-']").Filter(new() { HasText = HookPrefix }).First;
        for (var poll = 0; poll < 30; poll++)
        {
            if (await row.CountAsync() > 0 && (await row.GetByTestId("ops-delivery-status").InnerTextAsync()).Trim() == "succeeded")
            {
                return;
            }

            await session.Page.WaitForTimeoutAsync(500);
            await session.Page.GetByTestId("ops-refresh").ClickAsync();
        }

        (await row.GetByTestId("ops-delivery-status").InnerTextAsync()).Trim().ShouldBe("succeeded", "the hook's delivery ran within 15 seconds");
    }
}
