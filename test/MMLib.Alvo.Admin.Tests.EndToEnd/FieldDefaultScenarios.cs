namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Declaring what a create stores when the caller omits the field.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own world, and the reason is the working copy</b> — a copy is held per operator, so two
/// scenarios signing in as the same administrator compose one document between them and the second
/// one's apply carries the first one's edits.
/// </para>
/// <para>
/// <b>Driven at phone width.</b> The control is the widest thing the field editor draws — a label,
/// a box and a paragraph explaining what a default does — and the editor is the screen an operator
/// is most likely to reach for away from a desk. A leg that only ever ran at 1400 px would not have
/// caught the layout this whole panel was rebuilt for.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class FieldDefaultScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// A literal that the field's type cannot hold is refused in the editor, and the one it can hold
    /// reaches the descriptor, the plan and the applied schema.
    /// </summary>
    /// <remarks>
    /// The refusal leg comes first deliberately. The descriptor's <c>default</c> is a JSON literal and
    /// the apply refuses one whose kind the field cannot hold, so an editor that always wrote a string
    /// would compose a descriptor refused for a reason the operator never typed — reaching them as a
    /// failed apply rather than as a sentence beside the box.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_default_is_declared_in_the_editor_and_survives_to_the_applied_schema()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/schema/regions");

        /* The editor is a sheet now: it is on the page only while somebody is editing it. */
        await session.Page.ClickAsync("[data-testid='add-field']");
        await session.Page.Locator("[data-testid='field-sheet']").WaitForAsync();

        await session.Page.FillAsync("#new-field-name", "dispatch_note");
        await session.Page.ClickAsync(".a-choice button:has-text('integer')");
        await session.Page.FillAsync("#new-field-default", "not a number");
        await session.Page.ClickAsync("[data-testid='field-save']");

        await session.Page.GetByText("That field cannot be added").WaitForAsync();
        (await session.Page.Locator("main.a-content").InnerTextAsync())
            .ShouldContain("is not a number");

        /* Still on the entity: a refused default must not be a half-added field, and the editor is
           where the operator corrects it. */
        (session.Page.Url).ShouldContain("/schema/regions");
        await session.AssertNoHorizontalScrollAsync();

        await session.Page.ClickAsync(".a-choice button:has-text('string')");
        await session.Page.FillAsync("#new-field-default", "unassigned");
        await session.Page.ClickAsync("[data-testid='field-save']");
        await session.Page.WaitForURLAsync("**/schema/preview");

        /* Waiting for the plan control rather than for the URL alone: the navigation resolves before
           the preview's own render arrives, and the diff below is what that render draws. */
        await session.Page.Locator("button:has-text('Plan this change')").WaitForAsync();

        (await session.Page.Locator("main.a-content").InnerTextAsync())
            .ShouldContain("\"default\": \"unassigned\"");

        await session.Page.ClickAsync("button:has-text('Plan this change')");
        await session.Page.Locator("#apply-reason").WaitForAsync();
        await session.Page.FillAsync("#apply-reason", "Give regions a dispatch note");
        await session.Page.ClickAsync("button:has-text('Apply these changes')");
        await session.Page.GetByText("Applied as revision").First.WaitForAsync();

        await session.GoAsync("/schema/regions");
        (await session.Page.Locator("main.a-content").InnerTextAsync())
            .ShouldContain("default \"unassigned\"");

        /* The point of the whole feature: a create that omits the field stores the default rather
           than nothing. */
        await session.GoAsync("/data/regions");
        await session.Page.ClickAsync("button:has-text('New record')");
        await session.Page.Locator("#rf-name").WaitForAsync();
        await session.Page.FillAsync("#rf-name", "Northern");
        await session.Page.FillAsync("#rf-code", "NOR");
        await session.Page.ClickAsync("button:has-text('Create')");

        /* Visible, at this width, in whichever layout this width draws. The table is hidden below
           720 px and the row cards are hidden above it, so asserting on the table alone measured a
           layout the operator was not looking at — which is exactly how a phone came to show
           "showing 2 of 2" over an empty box while this suite stayed green. */
        var card = session.Page.Locator("[data-testid='row-card']").First;
        await card.WaitForAsync();

        var stored = await card.InnerTextAsync();
        stored.ShouldContain("Northern");
        stored.ShouldContain("unassigned");

        session.AssertConsoleClean();
    }
}
