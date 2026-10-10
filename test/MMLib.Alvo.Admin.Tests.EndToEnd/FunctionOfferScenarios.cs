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
        await AssertCheckKeepsFocusAsync(world, session, "hook-mutate-value-0", "replace(new.work_notes, search, replacement)", "search");
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
        await session.Page.FocusAsync("input#hook-condition");
        await session.Page.Keyboard.PressAsync("Home");

        var list = await OpenListAsync(session, "hook-condition");
        (await list.GetByTestId("fn-now").CountAsync()).ShouldBe(0, "now() is not a Condition function — the list has loaded, so this absence means it");
        await list.GetByTestId("fn-insert-size").ClickAsync();

        await WaitForValueAsync(session, "hook-condition", "size(text) > 3");
        await session.WaitForFocusOnAsync("hook-condition");
        (await Selection(session)).ShouldBe("text");
        await AssertCheckKeepsFocusAsync(world, session, "hook-condition", "size(text) > 3", "text");
        await session.Page.Keyboard.TypeAsync("new.last_name");

        await WaitForValueAsync(session, "hook-condition", "size(new.last_name) > 3");
        (await world.CheckedAsync("size(new.last_name) > 3")).Findings.ShouldBeEmpty("the verdict on exactly this text, not a count read right after typing");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Spec AC 4: the inserted placeholder is not a field, so the live check speaks — within 3 s — and focus and the
    /// selection stay where Insert put them. A check that took focus would make the operator click back before typing.
    /// </summary>
    /// <remarks>
    /// The check asserted is the one on the <paramref name="inserted"/> text, and its sentence must name the
    /// <paramref name="selected"/> placeholder: a sentence left over from an earlier text in the same box would otherwise
    /// satisfy the wait before the inserted text's check ever ran, and the focus assertion would prove nothing. The
    /// placeholder is matched as the check quotes it (<c>'search'</c>), never as a bare substring of the sentence.
    /// </remarks>
    internal static async Task AssertCheckKeepsFocusAsync(
        RecordingWorld world, AdminSession session, string inputId, string inserted, string selected)
    {
        await world.CheckedAsync(inserted);
        /* The placeholder as the check names it, quoted ("'text' is not a field of entity ..."): a bare substring would let a
           sentence that merely holds the word, such as one about a "text" or a "context", satisfy the wait. */
        await session.Page.GetByTestId($"check-{inputId}").Filter(new() { HasTextString = $"'{selected}'" }).First
            .WaitForAsync(new() { Timeout = 3_000 });

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

    /// <summary>Switches row <paramref name="index"/> to <paramref name="mode"/>, and returns once the switch is drawn.</summary>
    /// <remarks>
    /// <b>Not once the value box is there</b>: a string field's literal box and its expression box share the id
    /// <c>hook-mutate-value-{index}</c>, so a wait for the id answers at once, on the box the switch is replacing. A fill
    /// then lands in that old box, its input event names a handler the circuit has already dropped, and the text is lost
    /// — the check is never asked (FunctionListUnavailableScenarios failed so, 10 runs in 30 under load). The chip shows
    /// as chosen in the same render that draws the new box; <c>MutateEditingScenarios.ExpressionModeAsync</c> waits the
    /// same way for the same reason.
    /// </remarks>
    internal static Task MutateModeAsync(AdminSession session, int index, string mode)
        => ChooseChipAsync(session.Page.GetByTestId($"hook-mutate-mode-{index}"), mode);

    internal static Task TextModeAsync(AdminSession session) => ConditionModeAsync(session, "Text");

    /// <summary>Switches the condition to <paramref name="mode"/>, and returns once the switch is drawn, for <see cref="MutateModeAsync"/>'s reason.</summary>
    internal static Task ConditionModeAsync(AdminSession session, string mode)
        => ChooseChipAsync(session.Page.GetByTestId("hook-condition-mode"), mode);

    private static async Task ChooseChipAsync(ILocator group, string chip)
    {
        await group.GetByRole(AriaRole.Radio, new() { Name = chip, Exact = true }).ClickAsync();
        await group.GetByRole(AriaRole.Radio, new() { Name = chip, Exact = true, Checked = true }).WaitForAsync();
    }

    internal static Task<string> Selection(AdminSession session) =>
        session.Page.EvaluateAsync<string>("() => { const e = document.activeElement; return e.value.substring(e.selectionStart, e.selectionEnd); }");

    internal static ILocator Combobox(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });
}
