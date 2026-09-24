using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field editor writes nothing nobody chose, and shows what it draws no control for.
/// </summary>
/// <remarks>
/// Its own world, for <c>FieldDefaultScenarios</c>' reason: a working copy is held per operator and these stage
/// edits. It stops before the preview — what is under test is what the editor composes.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class FieldFacetScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>A new string is unbounded unless a limit is typed — no silent <c>maxLength: 120</c> (§8d item 15).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_string_field_is_unbounded_unless_a_max_length_is_typed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("dispatch_zone");
        (await sheet.GetByRole(AriaRole.Spinbutton, new() { Name = "Max length" }).InputValueAsync()).ShouldBeEmpty();
        await session.Page.GetByTestId("field-save").ClickAsync();

        var row = session.Page.GetByTestId("field-row-dispatch_zone");
        await row.WaitForAsync();
        (await row.InnerTextAsync()).ShouldNotContain("max");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// <c>work_orders.external_ref</c> is <c>readOnly: true</c>: the editor says so, and refuses <c>required</c>
    /// beside it as the apply would (<c>DescriptorValidator.cs:504</c>).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_read_only_facet_is_shown_and_required_beside_it_is_refused_in_the_editor()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("edit-field-external_ref").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        (await sheet.GetByTestId("field-note-readOnly").InnerTextAsync()).ShouldContain("kept exactly as declared");

        await sheet.GetByRole(AriaRole.Checkbox, new() { Name = "required", Exact = true }).CheckAsync();
        await session.Page.GetByTestId("field-save").ClickAsync();

        (await sheet.GetByTestId("error-panel").InnerTextAsync()).ShouldContain("readOnly");
        (await session.Page.GetByTestId("staged-external_ref").CountAsync()).ShouldBe(0, "a refused edit stages nothing");

        session.AssertConsoleClean();
    }
}
