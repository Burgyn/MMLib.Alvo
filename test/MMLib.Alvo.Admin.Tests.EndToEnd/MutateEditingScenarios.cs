using Microsoft.Playwright;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A mutate patches several fields, each with a value its type holds or a CEL expression (spec §4.3, ruling B3). Each
/// scenario works on a different entity of the bike-workshop descriptor, because the class shares one working copy.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class MutateEditingScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_mutate_sets_a_literal_and_an_expression_in_one_hook()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateAsync(session, "service_orders", "beforeUpdate");

        await session.ChooseAsync(Combobox(session, "Field 1"), "paid");
        await session.Page.GetByTestId("hook-mutate-value-0").GetByRole(AriaRole.Radio, new() { Name = "true", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-mutate-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Field 2"), "work_notes");
        await ExpressionModeAsync(session, 1);
        await session.Page.FillAsync("#hook-mutate-value-1", "'Paid in full'");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        var row = await session.Page.Locator("#hook-beforeUpdate-2").InnerTextAsync();
        row.ShouldContain("\"paid\": true");
        row.ShouldContain("\"work_notes\"");
        row.ShouldContain("'Paid in full'");

        await session.GoAsync("/changes");
        await session.WaitForPlanAsync();
        await session.Page.Locator("#apply-reason").WaitForAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "the apply accepts what the rows wrote");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_literal_that_does_not_fit_its_field_is_said_under_it_and_refused_on_submit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateAsync(session, "bikes", "beforeCreate");
        await session.ChooseAsync(Combobox(session, "Field 1"), "model_year");
        /* The library's list closes and its box is swapped for the one showing the value; focus stays on the select,
           inside the sheet, rather than falling to the page where Escape no longer reaches the sheet. */
        await session.WaitForFocusInsideAsync("hook-mutate-field-0");

        await session.Page.FillAsync("#hook-mutate-value-0", "abc");
        var fit = session.Page.GetByTestId("hook-mutate-fit-0");
        await fit.WaitForAsync();
        (await fit.InnerTextAsync()).ShouldContain("whole number");

        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await session.WaitForFocusInsideAsync("error-panel");
        (await editor.GetByTestId("error-panel").InnerTextAsync()).ShouldContain("whole number");

        await session.Page.FillAsync("#hook-mutate-value-0", "2026");
        await fit.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_enum_literal_is_chosen_from_its_declared_values()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateAsync(session, "rentals", "beforeUpdate");
        await session.ChooseAsync(Combobox(session, "Field 1"), "status");

        await session.ChooseAsync(Combobox(session, "Set field 1 to"), "returned");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#hook-beforeUpdate-0").InnerTextAsync()).ShouldContain("\"status\": \"returned\"");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_managed_or_computed_field_is_not_offered()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateAsync(session, "service_orders", "beforeCreate");

        await Combobox(session, "Field 1").ClickAsync();
        await Option(session, "status").WaitForAsync();
        (await Option(session, "status").CountAsync()).ShouldBe(1);
        (await Option(session, "total").CountAsync()).ShouldBe(0, "a computed field is maintained by the database");
        (await Option(session, "lines_count").CountAsync()).ShouldBe(0, "a rollup is maintained by the framework");
        (await Option(session, "created_at").CountAsync()).ShouldBe(0, "an audit column is the framework's");
        (await Option(session, "id").CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Two_rows_fit_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await NewMutateAsync(session, "customers", "beforeUpdate");
        await session.Page.GetByTestId("hook-mutate-add").ClickAsync();

        await session.AssertNoHorizontalScrollAsync();
    }

    /// <summary>
    /// Choosing mutate shows a blank row, and choosing reject again leaves nothing to lose: a blank row is no input, so
    /// Escape closes the sheet without asking (Task 8 review, minor 3).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_kind_round_trip_leaves_the_sheet_clean()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "order_lines");
        var editor = await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeCreate", 0);
        var actions = session.Page.GetByTestId("hook-actions");

        await actions.GetByRole(AriaRole.Radio, new() { Name = "mutate", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-mutate-row").First.WaitForAsync();
        await actions.GetByRole(AriaRole.Radio, new() { Name = "reject", Exact = true }).ClickAsync();
        await session.Page.Locator("#hook-reject").WaitForAsync();

        await session.WaitForFocusInsideAsync("hook-editor");
        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("editor-discard-question").CountAsync()).ShouldBe(0);
    }

    /// <summary>The edit sheet hands focus back to the Edit it was opened from, closed by Escape or by Save (spec §3.2).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Focus_returns_to_the_row_s_Edit_after_the_sheet_closes()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "service_orders");
        const string edit = "#hook-beforeDelete-0 [data-testid='hook-edit']";

        var editor = await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeDelete", 0);
        await session.WaitForFocusInsideAsync("hook-editor");
        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await WaitForFocusAsync(session, edit);

        editor = await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeDelete", 0);
        await session.Page.FillAsync("#hook-reject", "Cancel the order instead of deleting it.");
        await editor.GetByTestId("hook-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await WaitForFocusAsync(session, edit);
    }

    private static async Task NewMutateAsync(AdminSession session, string entity, string point)
    {
        await HookEditInPlaceScenarios.OnWriteAsync(session, entity);
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = point, Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = "mutate", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-mutate-row").First.WaitForAsync();
    }

    /// <summary>Switches row <paramref name="index"/> to take an expression, and returns once its expression box is drawn.</summary>
    /// <remarks>
    /// The value box keeps its id across the switch but is a new element, so a fill before the switch is drawn lands in
    /// the old box: its input event names a handler the circuit has already dropped, and the text is lost. The chip
    /// shows as chosen in the same render that draws the new box.
    /// </remarks>
    internal static async Task ExpressionModeAsync(AdminSession session, int index)
    {
        var modes = session.Page.GetByTestId($"hook-mutate-mode-{index}");
        await modes.GetByRole(AriaRole.Radio, new() { Name = "an expression", Exact = true }).ClickAsync();
        await modes.GetByRole(AriaRole.Radio, new() { Name = "an expression", Exact = true, Checked = true }).WaitForAsync();
    }

    private static async Task WaitForFocusAsync(AdminSession session, string selector)
        => await session.Page.WaitForFunctionAsync("selector => !!document.activeElement?.matches(selector)", selector);

    internal static ILocator Combobox(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });

    private static ILocator Option(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Option, new() { Name = name, Exact = true });
}

/// <summary>
/// A row that cannot be written says so the moment its hook opens, not only on Save — its own world, because the descriptor
/// carrying the row arrives by import, which replaces the working copy every other scenario of a world shares.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class MutateOpenScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    /// <summary>
    /// A declared <c>"3"</c> on a decimal field reads as the text 3, which the field would take, so Save would write the
    /// number 3 — a change nobody made. The row says so as the hook opens, and typing the value again writes it as a number.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_literal_declared_as_another_json_kind_is_refused_as_soon_as_its_hook_opens()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var descriptor = HookShapeScenarios.Importable();
        descriptor["entities"]!["rentals"]!["hooks"]!["beforeUpdate"] = JsonNode.Parse(
            """[ { "action": { "mutate": { "days": "3" } } } ]""");
        await session.GoAsync("/transfer");
        await session.Page.FillAsync("#import-json", descriptor.ToJsonString());
        await session.Page.Locator("#import-json").PressAsync("Meta+Enter");
        await session.Page.WaitForURLAsync("**/changes");

        await HookEditInPlaceScenarios.OnWriteAsync(session, "rentals");
        await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeUpdate", 0);
        var fit = session.Page.GetByTestId("hook-mutate-fit-0");
        await fit.WaitForAsync();
        (await fit.InnerTextAsync()).ShouldContain("'days' is declared as a string");
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "said under the row, before any Save");

        await session.Page.FillAsync("#hook-mutate-value-0", "4");
        await fit.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }
}
