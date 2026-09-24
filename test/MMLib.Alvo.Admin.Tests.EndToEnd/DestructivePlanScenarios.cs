using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Planning a change that destroys data.
/// </summary>
/// <remarks>
/// <b>Its own world, and the reason is the working copy.</b> A copy is held per operator, not per
/// scenario, so two scenarios signing in as the same administrator compose one document between
/// them — and the second one's apply then carries the first one's edits. Separate fixtures are what
/// keep each of these measuring its own change.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class DestructivePlanScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// A destructive change can be planned, and planning it is not allowing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the deadlock the editor exposed. The dry run was sent with the operator's
    /// confirmation flag, so a drop was refused <em>before</em> a plan existed; the refusal said to
    /// type the project's name "above", and that control renders only once a plan is there. A
    /// destructive change could therefore be composed in the dashboard and never reviewed or applied
    /// from it.
    /// </para>
    /// <para>
    /// A dry run now asks with destruction allowed, because describing a drop destroys nothing, and
    /// the apply still asks with what was actually confirmed — which is what this scenario measures:
    /// the plan is readable, and the apply goes through a confirm rather than straight to the database.
    /// </para>
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Dropping_a_field_reaches_the_preview_and_not_the_database()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.ClickAsync("[data-testid='remove-field-code']");
        await session.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();
        await session.PreviewPendingAsync();
        await session.Page.GetByText("against the database").First.WaitForAsync();

        var plan = await session.Content.InnerTextAsync();
        plan.ShouldContain("code");
        plan.ShouldContain("destroys");

        /* The confirmation is the apply's, not the editor's: the plan destroys data, so the apply
           opens a confirm that waits for the project's name. That is where a dropped column is
           confirmed, and nothing has reached the database on the way here. */
        (await session.Page.GetByTestId("plan-destroys").IsVisibleAsync()).ShouldBeTrue();
        await session.Button("Apply these changes").ClickAsync();
        await session.Dialog("apply-confirm").WaitForAsync();
        await session.Dialog("apply-confirm").GetByTestId("apply-confirm-cancel").ClickAsync();
        await session.Dialog("apply-confirm").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        session.AssertConsoleClean();
    }
}

/// <summary>
/// A destructive apply waits for the project's name typed exactly, and Escape leaves it unapplied (spec §3.2).
/// </summary>
/// <remarks>
/// Its own world although it applies nothing: it stages the same drop as <see cref="DestructivePlanScenarios"/>, and
/// one operator's working copy is shared by every scenario of a world, so the second of the two would find the field
/// already gone.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class DestructiveApplyConfirmScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_destructive_apply_cannot_run_until_the_project_name_is_typed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("remove-field-code").ClickAsync();
        await session.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();
        await session.PreviewPendingAsync();

        (await session.Page.GetByTestId("plan-destroys").GetAttributeAsync("role")).ShouldBe("alert");
        await session.Button("Apply these changes").ClickAsync();

        var confirm = session.Dialog("apply-confirm");
        await confirm.WaitForAsync();
        (await confirm.GetAttributeAsync("aria-modal")).ShouldBe("true");
        await EditorScenarios.WaitForFocusOnAsync(session, "confirm-name");
        (await confirm.GetByTestId("apply-confirm-run").IsDisabledAsync()).ShouldBeTrue();

        await session.Page.Keyboard.TypeAsync("field-servic");
        (await confirm.GetByTestId("apply-confirm-run").IsDisabledAsync()).ShouldBeTrue("almost the name is not the name");
        await session.Page.Keyboard.TypeAsync("e");
        await session.Page.WaitForFunctionAsync(
            "() => !document.querySelector(\"[data-testid='apply-confirm-run']\")?.disabled");

        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByText("Applied as revision").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
}
