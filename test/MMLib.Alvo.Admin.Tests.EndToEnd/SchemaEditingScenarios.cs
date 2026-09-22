namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Changing a field the descriptor already declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Issue #229's own words decide that this belongs here:</b> <i>"change a field or a rule is
/// deliverable without #103; add an entity in the UI is not."</i> What shipped first was the
/// addition — the half that needs a new route — while editing a declared field, the half that
/// needs nothing, had no control at all. A reader of the Fields tab could see every facet and
/// change none of them.
/// </para>
/// <para>
/// <b>Its own world, and one apply in it.</b> <c>ChangeTheBackendScenarios</c> records that a
/// second apply does not finish under the in-process host; this class therefore takes a fresh
/// fixture rather than adding an apply to that one.
/// </para>
/// <para>
/// The facet changed is a widening — a longer <c>maxLength</c> — because it is the change whose
/// plan is unambiguous. Narrowing one, or making a column required, is a change whose cost depends
/// on the rows already in the table, and the screen under test is the editor rather than the
/// migration guard.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class SchemaEditingScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_declared_fields_facet_can_be_changed_previewed_and_applied()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        (await session.Page.Locator("main.a-content").InnerTextAsync()).ShouldContain("max 160");

        await session.Page.ClickAsync("[data-testid='edit-field-email']");

        /* Waiting for the heading rather than for the max-length input: that input is in the panel
           in both modes, so waiting on it is waiting for something that is already there — the
           assertions below would then read the panel before the click's re-render arrived. */
        await session.Page.GetByText("Edit email").WaitForAsync();

        /* The name is the field's identity in the document, so the editor locks it: replacing a key
           is a drop and a create, which is not what "edit" means to the person clicking it. */
        (await session.Page.Locator("#new-field-name").IsDisabledAsync()).ShouldBeTrue();
        (await session.Page.Locator("#new-field-name").InputValueAsync()).ShouldBe("email");

        await session.Page.FillAsync("#new-field-max", "200");
        await session.Page.ClickAsync("[data-testid='field-save']");
        await session.Page.WaitForURLAsync("**/schema/preview");

        await session.Page.ClickAsync("button:has-text('Plan this change')");

        /* Not "N steps against the database": on SQLite a longer maxLength is no DDL at all, because
           TEXT carries no length — so the honest signal that the plan came back is the apply control
           appearing, whether or not the plan has steps in it. */
        await session.Page.Locator("#apply-reason").WaitForAsync();
        await session.Page.FillAsync("#apply-reason", "Widen the customer email column");
        await session.Page.ClickAsync("button:has-text('Apply these changes')");
        await session.Page.GetByText("Applied as revision").First.WaitForAsync();

        await session.GoAsync("/schema/customers");
        (await session.Page.Locator("main.a-content").InnerTextAsync()).ShouldContain("max 200");

        /* The facets the editor cannot draw must survive it. `email` carries a description and a
           `format`, and an editor that rebuilt the declaration from its own controls would drop both
           — silently, with the apply carrying on working, which is exactly the narrowing §4.6
           forbids of the export. */
        await session.GoAsync("/schema/transfer");
        var descriptor = await session.Page.Locator("main.a-content").InnerTextAsync();
        descriptor.ShouldContain("Where to send correspondence");
        descriptor.ShouldContain("\"format\": \"email\"");

        session.AssertConsoleClean();
    }
}
