using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The hook editor offers the CEL functions a box's slot admits, and Insert writes a call at the caret with its first
/// placeholder selected (spec §9). Built-ins only — the host-function proof is <c>HostFunctionScenarios</c>.
/// Each scenario works on a different entity: the class shares one working copy and nothing here is added.
/// </summary>
/// <param name="world">The running host and browser, recording what the dashboard asks.</param>
public sealed class FunctionOfferScenarios(RecordingWorld world) : IClassFixture<RecordingWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_mutate_value_offers_mutate_functions_and_insert_selects_the_first_placeholder()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateExpressionAsync(session, "service_orders", "beforeUpdate", "work_notes");

        var list = await OpenListAsync(session, "hook-mutate-value-0");
        (await list.GetByTestId("fn-now").CountAsync()).ShouldBe(1, "now() is a Mutate function");
        (await list.GetByTestId("fn-math.round").InnerTextAsync()).ShouldContain("math.round(x: Decimal, digits: Int) -> Decimal");
        (await list.GetByTestId("fn-math.round").InnerTextAsync()).ShouldContain("built-in");

        await list.GetByTestId("fn-insert-replace").ClickAsync();

        await WaitForValueAsync(session, "hook-mutate-value-0", "replace(new.work_notes, search, replacement)");
        await session.WaitForFocusOnAsync("hook-mutate-value-0");
        (await Selection(session)).ShouldBe("search");
        await AssertCheckKeepsFocusAsync(session, "hook-mutate-value-0", "search");
        await session.Page.Keyboard.TypeAsync("'-'");
        await WaitForValueAsync(session, "hook-mutate-value-0", "replace(new.work_notes, '-', replacement)");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_condition_offers_condition_functions_and_insert_lands_at_the_caret()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewHookAsync(session, "customers", "beforeCreate", "reject");
        await TextModeAsync(session);
        await session.Page.FillAsync("input#hook-condition", " > 3");
        await session.Page.FocusAsync("#hook-condition");
        await session.Page.Keyboard.PressAsync("Home");

        var list = await OpenListAsync(session, "hook-condition");
        (await list.GetByTestId("fn-now").CountAsync()).ShouldBe(0, "now() is not a Condition function — the list has loaded, so this absence means it");
        await list.GetByTestId("fn-insert-size").ClickAsync();

        await WaitForValueAsync(session, "hook-condition", "size(text) > 3");
        await session.WaitForFocusOnAsync("hook-condition");
        (await Selection(session)).ShouldBe("text");
        await AssertCheckKeepsFocusAsync(session, "hook-condition", "text");
        await session.Page.Keyboard.TypeAsync("new.last_name");

        await WaitForValueAsync(session, "hook-condition", "size(new.last_name) > 3");
        (await world.CheckedAsync("size(new.last_name) > 3")).Findings.ShouldBeEmpty("the verdict on exactly this text, not a count read right after typing");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Spec AC 4: the inserted placeholder is not a field, so the live check speaks — within 3 s — and focus and the
    /// selection stay where Insert put them. A check that took focus would make the operator click back before typing.
    /// </summary>
    internal static async Task AssertCheckKeepsFocusAsync(AdminSession session, string inputId, string selected)
    {
        await session.Page.GetByTestId($"check-{inputId}").First.WaitForAsync(new() { Timeout = 3_000 });

        (await session.Page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''")).ShouldBe(inputId, "the check sentence never takes focus");
        (await Selection(session)).ShouldBe(selected, "nor the selection");
    }

    /// <summary>
    /// Waits until the box <paramref name="id"/> holds <paramref name="value"/>: Insert is a round trip to the circuit, so a
    /// value read right after the click is the old one (spec D-10 — wait for what is asserted).
    /// </summary>
    internal static async Task WaitForValueAsync(AdminSession session, string id, string value)
    {
        try
        {
            await session.Page.WaitForFunctionAsync(
                "([id, value]) => document.getElementById(id)?.value === value", new[] { id, value }, new() { PollingInterval = 100, Timeout = 10_000 });
        }
        catch (TimeoutException)
        {
            (await session.Page.EvaluateAsync<string>("id => document.getElementById(id)?.value ?? '(no box)'", id)).ShouldBe(value);
            throw;
        }
    }

    internal static async Task<ILocator> OpenListAsync(AdminSession session, string inputId)
    {
        var list = session.Page.GetByTestId($"fn-list-{inputId}");
        await list.Locator("summary").ClickAsync();
        await list.Locator("[data-testid^='fn-insert-']").First.WaitForAsync();
        return list;
    }

    internal static async Task NewHookAsync(AdminSession session, string entity, string point, string kind)
    {
        await HookEditInPlaceScenarios.OnWriteAsync(session, entity);
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = point, Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = kind, Exact = true }).ClickAsync();
    }

    internal static async Task NewMutateExpressionAsync(AdminSession session, string entity, string point, string field)
    {
        await NewHookAsync(session, entity, point, "mutate");
        await session.ChooseAsync(Combobox(session, "Field 1"), field);
        await MutateModeAsync(session, 0, "an expression");
        await session.Page.Locator("#hook-mutate-value-0").WaitForAsync();
    }

    internal static Task MutateModeAsync(AdminSession session, int index, string mode) =>
        session.Page.GetByTestId($"hook-mutate-mode-{index}").GetByRole(AriaRole.Radio, new() { Name = mode, Exact = true }).ClickAsync();

    internal static Task TextModeAsync(AdminSession session) => ConditionModeAsync(session, "Text");

    internal static Task ConditionModeAsync(AdminSession session, string mode) =>
        session.Page.GetByTestId("hook-condition-mode").GetByRole(AriaRole.Radio, new() { Name = mode, Exact = true }).ClickAsync();

    internal static Task<string> Selection(AdminSession session) =>
        session.Page.EvaluateAsync<string>("() => { const e = document.activeElement; return e.value.substring(e.selectionStart, e.selectionEnd); }");

    internal static ILocator Combobox(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });
}
