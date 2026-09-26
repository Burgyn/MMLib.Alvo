using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field editor's number boxes refuse what they cannot read, at the box, with focus on it (spec §3.3, §3.8): a
/// max length of <c>0</c>, or a precision of <c>1e</c>, is never staged as "no limit" or as the default in its place.
/// </summary>
/// <remarks>Its own world: the last step stages a field, and the working copy is one per operator.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class FacetNumberScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_number_box_the_editor_cannot_read_is_refused_at_the_box_and_nothing_is_staged()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Dialog("field-sheet");
        await sheet.Locator("#new-field-name").FillAsync("nickname");

        await sheet.Locator("#new-field-max").FillAsync("0");
        await sheet.GetByTestId("field-save").ClickAsync();
        await RefusedWithFocusAsync(session, sheet, "new-field-max");

        await sheet.GetByRole(AriaRole.Radio, new() { Name = "decimal", Exact = true }).ClickAsync();
        await sheet.Locator("#new-field-precision").FillAsync("1e");
        await sheet.GetByTestId("field-save").ClickAsync();
        await RefusedWithFocusAsync(session, sheet, "new-field-precision");
        (await session.SnackbarCountAsync()).ShouldBe(0, "nothing was staged, and a refusal is never a snackbar");

        await sheet.Locator("#new-field-precision").FillAsync("12");
        await sheet.GetByTestId("field-save").ClickAsync();
        await session.SnackbarAsync("added to the working copy");
        session.AssertConsoleClean();
    }

    private static async Task RefusedWithFocusAsync(AdminSession session, ILocator sheet, string id)
    {
        await sheet.Locator($"#{id}-problem").WaitForAsync();
        (await sheet.Locator($"#{id}").GetAttributeAsync("aria-invalid")).ShouldBe("true");
        (await sheet.Locator($"#{id}").GetAttributeAsync("aria-describedby")).ShouldBe($"{id}-hint {id}-problem");
        await session.Page.WaitForFunctionAsync("id => document.activeElement?.id === id", id);
    }
}
