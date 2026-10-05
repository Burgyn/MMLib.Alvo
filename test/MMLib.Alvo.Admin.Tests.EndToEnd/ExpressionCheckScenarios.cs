using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>What apply would say about an expression is shown under its input while it is typed, without taking focus.</summary>
/// <param name="world">The running host and browser.</param>
public sealed class ExpressionCheckScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_rule_naming_an_undeclared_role_is_flagged_and_the_flag_clears_when_it_is_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("Rules");
        var pending = await PendingTextAsync(session);

        await session.Page.FillAsync("#rule-list", "'amdin' in @user.roles");
        var finding = session.Page.GetByTestId("check-rule-list");
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("amdin");
        await ShouldKeepFocusAsync(session, "rule-list");
        (await PendingTextAsync(session)).ShouldBe(pending, "a check never dirties the working copy");

        await session.Page.FillAsync("#rule-list", "'admin' in @user.roles");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session, "rule-list");
        await session.Page.GetByTestId("rule-dirty-list").WaitForAsync();
        (await PendingTextAsync(session)).ShouldBe(pending, "nor does clearing one");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Escape_puts_back_the_saved_rule_and_the_flag_for_the_discarded_draft_goes()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("Rules");
        var declared = await session.Page.InputValueAsync("#rule-get");

        await session.Page.FillAsync("#rule-get", "'amdin' in @user.roles");
        var finding = session.Page.GetByTestId("check-rule-get");
        await finding.WaitForAsync(new() { Timeout = 3_000 });

        await session.Page.Keyboard.PressAsync("Escape");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        (await session.Page.InputValueAsync("#rule-get")).ShouldBe(declared);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_flagged_rule_can_still_be_saved()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("Rules");

        await session.Page.FillAsync("#rule-update", "'amdin' in @user.roles");
        await session.Page.GetByTestId("check-rule-update").WaitForAsync(new() { Timeout = 3_000 });
        var save = session.Page.GetByTestId("rule-save-update");
        (await save.IsEnabledAsync()).ShouldBeTrue("a check never gates Save");

        await save.ClickAsync();

        await session.SnackbarAsync("Rule saved to the working copy");
        await session.Page.GetByTestId("pending-bar").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_condition_naming_an_undeclared_field_is_flagged_and_the_flag_clears_when_it_is_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("On write");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        /* The sheet takes focus a render after it shows (AdminSession.WaitForFocusInsideAsync); typed into before then, the
           box loses focus to the sheet's first control, and the focus asserted below is the sheet's, not the check's. */
        await session.WaitForFocusInsideAsync("hook-editor", FocusScope.Dialog);

        await session.Page.FillAsync("#hook-condition", "new.no_such_field == 'priority'");
        var finding = session.Page.GetByTestId("check-hook-condition");
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("no_such_field");
        await ShouldKeepFocusAsync(session, "hook-condition");

        await session.Page.FillAsync("#hook-condition", "new.tier == 'priority'");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session, "hook-condition");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_mutate_value_naming_an_undeclared_field_is_flagged_and_the_flag_clears_when_it_is_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("On write");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.WaitForFocusInsideAsync("hook-editor", FocusScope.Dialog);
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = "mutate", Exact = true }).ClickAsync();
        await ExpressionRowAsync(session, 0, "name");

        await session.Page.FillAsync("#hook-mutate-value-0", "new.no_such_field");
        var finding = session.Page.GetByTestId("check-hook-mutate-value-0").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("no_such_field");
        await ShouldKeepFocusAsync(session, "hook-mutate-value-0");

        await session.Page.FillAsync("#hook-mutate-value-0", "new.code");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        (await session.Page.GetByTestId("check-hook-mutate-value-0").CountAsync()).ShouldBe(0, "every sentence goes, not only the first");
        await ShouldKeepFocusAsync(session, "hook-mutate-value-0");
    }

    /// <summary>
    /// A condition that does not compile stops the before-hook compiler before the value, so the value is checked in a
    /// hook without its condition: a broken condition must never mask a broken value.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_broken_mutate_value_is_flagged_even_while_the_condition_is_broken_too()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("On write");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = "mutate", Exact = true }).ClickAsync();
        await ExpressionRowAsync(session, 0, "name");
        await session.Page.FillAsync("#hook-condition", "new.code ==");
        await session.Page.GetByTestId("check-hook-condition").First.WaitForAsync(new() { Timeout = 3_000 });

        await session.Page.FillAsync("#hook-mutate-value-0", "nope(");

        var value = session.Page.GetByTestId("check-hook-mutate-value-0").First;
        await value.WaitForAsync(new() { Timeout = 3_000 });
        var flagged = await value.InnerTextAsync();
        await session.Page.FillAsync("#hook-condition", "new.code == 'a'");
        await session.Page.GetByTestId("check-hook-condition").First.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        (await session.Page.GetByTestId("check-hook-mutate-value-0").CountAsync()).ShouldBeGreaterThan(0, "the value is still broken");
        (await value.InnerTextAsync()).ShouldBe(flagged, "a condition edit leaves the value's flag shown as it was (pre-flight C4)");
    }

    /// <summary>
    /// A check that cannot be asked — the patched field is empty, so the value has no slot — shows nothing: never the
    /// stale flag about text no longer in the box (spec §4.3, §5 criterion 4).
    /// </summary>
    /// <remarks>
    /// A field select cannot be emptied, so the empty field is reached the way rows still reach one: a second row whose
    /// field is not chosen yet moves into the first row's place when that row is removed, and with it the box's check key.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_flag_goes_when_the_next_check_cannot_be_asked_rather_than_stay_stale()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("On write");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = "mutate", Exact = true }).ClickAsync();
        await ExpressionRowAsync(session, 0, "name");
        await session.Page.FillAsync("#hook-mutate-value-0", "new.no_such_field");
        var finding = session.Page.GetByTestId("check-hook-mutate-value-0").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });

        await session.Page.GetByTestId("hook-mutate-add").ClickAsync();
        await ExpressionRowAsync(session, 1, field: null);
        await session.Page.FillAsync("#hook-mutate-value-1", "new.code");
        await session.Page.GetByTestId("hook-mutate-remove-0").ClickAsync();

        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        (await session.Page.InputValueAsync("#hook-mutate-value-0")).ShouldBe("new.code", "the second row is now the first");
        (await session.Page.Locator("[data-testid^='check-hook-mutate-value']").CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_computed_expression_naming_an_undeclared_field_is_flagged_and_the_flag_clears_when_it_is_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("double_price");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "decimal", Exact = true }).ClickAsync();

        await session.Page.FillAsync("#new-field-computed", "no_such_field * 2");
        var finding = session.Page.GetByTestId("check-new-field-computed").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("no_such_field");
        await ShouldKeepFocusAsync(session, "new-field-computed");

        await session.Page.FillAsync("#new-field-computed", "quoted_price * quoted_price");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session, "new-field-computed");
    }

    /// <summary>A field default is a literal, never an expression, so it is deliberately not checked (spec §4.2).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_field_default_is_not_checked_even_when_it_is_written_as_an_expression()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("dispatch_note");

        await session.Page.FillAsync("#new-field-default", """{"$cel": "no_such_field"}""");

        /* The default box is only drawn for a field callers write, so the pipeline is proved live on the computed box,
           which is checked; by the time its sentence is up, a check of the default (same debounce, same call) would be too. */
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "decimal", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#new-field-computed", "no_such_field * code");
        await session.Page.GetByTestId("check-new-field-computed").First.WaitForAsync(new() { Timeout = 3_000 });

        await sheet.GetByRole(AriaRole.Radio, new() { Name = "written by callers", Exact = true }).ClickAsync();
        (await session.Page.InputValueAsync("#new-field-default")).ShouldBe("""{"$cel": "no_such_field"}""");
        (await session.Page.Locator("[data-testid^='check-']").CountAsync()).ShouldBe(0, "no input of the form shows a check sentence, the default included");
    }

    /// <summary>"Not checked yet" is calm: a box whose draft is refused elsewhere is muted, not flagged red.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_box_checked_while_the_draft_is_refused_elsewhere_says_not_checked_yet_muted()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("On write");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = "afterCreate", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = "webhook", Exact = true }).ClickAsync();
        /* The endpoint is picked from the declared ones now, and this world declares none, so the draft is refused elsewhere:
           at its action, which has no endpoint and a payload over the schema's 8000 characters (plan Task 10). The schema's
           action is a oneOf, so either is reported on the action itself, never on action/payload. */
        await session.Page.FillAsync("#hook-payload", "[" + new string('1', 8000) + "]");

        await session.Page.FillAsync("#hook-condition", "new.tier == 'priority'");

        var finding = session.Page.GetByTestId("check-hook-condition").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        var said = await finding.InnerTextAsync();
        said.ShouldStartWith("Not checked yet");
        said.ShouldContain("('/entities/customers/hooks/afterCreate/0/action')", Case.Sensitive,
            "it names the part of the draft that is refused, this hook's action, and nothing else");
        (await finding.GetAttributeAsync("class"))!.ShouldContain("a-field__problem--muted");
    }

    /// <summary>Sets mutate row <paramref name="index"/> to take an expression, after choosing its field when one is given.</summary>
    private static async Task ExpressionRowAsync(AdminSession session, int index, string? field)
    {
        if (field is not null)
        {
            await session.ChooseAsync(MutateEditingScenarios.Combobox(session, $"Field {index + 1}"), field);
        }

        await MutateEditingScenarios.ExpressionModeAsync(session, index);
    }

    /// <summary>What the pending bar says; a check stages nothing, so it must not move.</summary>
    private static async Task<string> PendingTextAsync(AdminSession session)
    {
        var bar = session.Page.GetByTestId("pending-bar");
        return await bar.CountAsync() == 0 ? string.Empty : await bar.InnerTextAsync();
    }

    private static async Task ShouldKeepFocusAsync(AdminSession session, string id)
        => (await session.Page.EvaluateAsync<string>("document.activeElement?.id ?? ''")).ShouldBe(id);
}
