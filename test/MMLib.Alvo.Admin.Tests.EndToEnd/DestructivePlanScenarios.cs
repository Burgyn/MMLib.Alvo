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
    /// the plan is readable and the apply button is not available.
    /// </para>
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Dropping_a_field_reaches_the_preview_and_not_the_database()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.ClickAsync("[data-testid='remove-field-code']");
        await session.PreviewPendingAsync();
        await session.Page.GetByText("against the database").First.WaitForAsync();

        var plan = await session.Page.Locator("main.a-content").InnerTextAsync();
        plan.ShouldContain("code");
        plan.ShouldContain("destroys");

        /* The confirmation is the apply's, not the editor's: the plan destroys data, so the apply
           button stays disabled until the project's name is typed into the guard below it. That is
           where a dropped column is confirmed, and nothing has reached the database on the way
           here. */
        (await session.Page.Locator("button:has-text('Apply these changes')").IsDisabledAsync())
            .ShouldBeTrue();

        session.AssertConsoleClean();
    }
}
