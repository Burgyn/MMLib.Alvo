using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A rule is staged by Save or Ctrl/Cmd+Enter, never by leaving the box (spec §3.1, §3.4; defect #7).</summary>
/// <remarks>
/// Its own world. Its first scenario stages a rule on <c>customers</c>, so the others prove "nothing was staged" by
/// the rule they typed into, on other entities, rather than by the pending bar.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RuleEditingScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Leaving_a_changed_rule_stages_nothing_and_Ctrl_Enter_saves_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("Rules");

        await session.Page.FillAsync("#rule-list", "'admin' in @user.roles");
        await session.Page.GetByTestId("rule-dirty-list").WaitForAsync();
        await session.Page.Locator("#rule-get").FocusAsync();
        await session.SettleAsync();
        (await session.Page.GetByTestId("pending-bar").CountAsync()).ShouldBe(0, "leaving the box saves nothing");

        await session.Page.Locator("#rule-list").FocusAsync();
        await session.Page.Keyboard.PressAsync("Control+Enter");

        await session.SnackbarAsync("Rule saved to the working copy");
        await session.Page.GetByTestId("rule-dirty-list").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.GetByTestId("pending-bar").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Enter_is_a_newline_and_Escape_puts_back_what_the_descriptor_says()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("Rules");
        var declared = await session.Page.InputValueAsync("#rule-get");

        await session.Page.Locator("#rule-get").FocusAsync();
        await session.Page.Keyboard.PressAsync("End");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.Keyboard.TypeAsync("|| false");
        (await session.Page.InputValueAsync("#rule-get")).ShouldContain("\n");
        await session.Page.GetByTestId("rule-dirty-get").WaitForAsync();

        await session.Page.Keyboard.PressAsync("Escape");
        await session.Page.WaitForFunctionAsync(
            "([id, value]) => document.getElementById(id)?.value === value", new[] { "rule-get", declared });
    }

    /// <summary>
    /// A rule left unsaved while the operator looks at another tab is still there, still marked unsaved, on the way
    /// back: the drafts belong to the entity screen, not to the tab's body.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_unsaved_rule_survives_a_move_to_another_tab_and_stays_marked()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("Rules");
        await session.Page.FillAsync("#rule-delete", "false");
        await session.Page.GetByTestId("rule-dirty-delete").WaitForAsync();

        await session.OpenTabAsync("Fields");
        await session.OpenTabAsync("Rules");

        await session.Page.GetByTestId("rule-dirty-delete").WaitForAsync();
        (await session.Page.InputValueAsync("#rule-delete")).ShouldBe("false");
        (await session.Page.GetByTestId("rule-save-delete").IsEnabledAsync()).ShouldBeTrue("moving between tabs saves nothing");
    }

    /// <summary>
    /// Leaving the entity with an unsaved rule asks first; Keep stays with the text, Discard leaves without it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Leaving_the_entity_with_an_unsaved_rule_asks_first()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("Rules");
        var declared = await session.Page.InputValueAsync("#rule-update");
        await session.Page.FillAsync("#rule-update", "false");
        await session.Page.GetByTestId("rule-dirty-update").WaitForAsync();
        var schema = session.Content.GetByRole(AriaRole.Link, new() { Name = "Schema", Exact = true });

        await schema.ClickAsync();
        await session.Dialog("unsaved-rules").GetByTestId("unsaved-rules-keep").ClickAsync();
        await session.Page.GetByTestId("unsaved-rules").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.Page.Url.ShouldContain("/schema/work_orders");
        (await session.Page.InputValueAsync("#rule-update")).ShouldBe("false");

        await schema.ClickAsync();
        await session.Dialog("unsaved-rules").GetByTestId("unsaved-rules-discard").ClickAsync();
        await session.Page.WaitForURLAsync("**/admin/schema");

        await session.GoAsync("/schema/work_orders?tab=rules");
        (await session.Page.InputValueAsync("#rule-update")).ShouldBe(declared, "discarding a draft stages nothing");
    }
}

/// <summary>The Save rule button stages the rule, and is only offered while there is something to save.</summary>
/// <remarks>Its own world: it stages a rule, and <see cref="RuleEditingScenarios"/>' first scenario asserts nothing is staged yet.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RuleSaveScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Save_rule_is_offered_once_the_rule_changes_and_stages_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("Rules");
        var save = session.Page.GetByTestId("rule-save-create");
        (await save.IsDisabledAsync()).ShouldBeTrue("nothing to save yet");

        await session.Page.FillAsync("#rule-create", "'admin' in @user.roles");
        await session.Page.WaitForFunctionAsync("() => !document.querySelector(\"[data-testid='rule-save-create']\").disabled");
        await save.ClickAsync();

        await session.SnackbarAsync("Rule saved to the working copy");
        await session.Page.GetByTestId("rule-dirty-create").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.InputValueAsync("#rule-create")).ShouldBe("'admin' in @user.roles");
        await session.Page.GetByTestId("pending-bar").WaitForAsync();
    }
}
