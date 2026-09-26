using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A created item appears in place, scrolled to and lit, the same way on every list (spec §3.5; final review M3). The
/// entity, index, hook and person lists are pinned where they are made (<c>SchemaListScenarios</c>,
/// <c>PersonEditorScenarios</c>), the record in <c>RecordPlacementScenarios</c>; this is the field.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class CreatedItemScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>A new field is scrolled to and lit, not only badged "new".</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_created_field_is_scrolled_to_and_lit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.SetViewportSizeAsync(1280, 600);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.FillAsync("#new-field-name", "arrival_note");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.SnackbarAsync("Saved to the working copy");

        var row = session.Page.GetByTestId("field-row-arrival_note");
        await session.WaitForInViewAsync(row);
        (await row.GetAttributeAsync("data-alvo-new")).ShouldBe("true", "a created field is lit as every created item is");
        (await session.Page.Locator("[data-alvo-new]").CountAsync()).ShouldBe(1, "only the new field");
        session.AssertConsoleClean();
    }
}
