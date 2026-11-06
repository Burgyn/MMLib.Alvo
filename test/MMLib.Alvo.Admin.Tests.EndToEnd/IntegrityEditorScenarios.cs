using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// HX-11's dashboard slice: a field's <c>validation</c> is typed in the field editor, checked live against the real
/// host, applied, and enforced by the database.
/// </summary>
/// <remarks>
/// The bike-workshop <c>parts.unit_price</c> — a written decimal on a global entity, so the check needs no tenant. Its
/// own world: it applies.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class IntegrityEditorValidationScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task IntegrityEditor_a_validation_is_checked_live_applied_and_enforced()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var sheet = await IntegrityEditor.OpenFieldAsync(session, "parts", "unit_price");

        var box = sheet.GetByTestId("field-validation");
        await box.FillAsync("value >= 0 && @user.id == @user.id");
        var finding = session.Page.GetByTestId("check-field-validation").First;
        await finding.WaitForAsync(new() { Timeout = 5_000 });
        (await finding.InnerTextAsync()).ShouldContain("cannot be a CHECK constraint");
        await box.FillAsync("value >= 0");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 5_000 });

        await IntegrityEditor.SaveAndApplyAsync(session, "unit_price", "Prices are never negative");

        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.CreateAsync(
            "parts", NegativePart(), AlvoContext.System(tenant: null), cancellationToken: TestContext.Current.CancellationToken));

        session.AssertConsoleClean();
    }

    private static Dictionary<string, object?> NegativePart() => new(StringComparer.Ordinal)
    {
        ["sku"] = "SKU-NEG-1",
        ["name"] = "A bolt sold at a loss",
        ["brand"] = "Generic",
        ["category"] = "consumables",
        ["unit_price"] = -1m,
        ["purchase_price"] = 0.5m,
    };
}

/// <summary>
/// HX-11's dashboard slice: a rollup's <c>where</c> is typed in the rollup section, checked live against the child's
/// fields, applied, and reopened as declared.
/// </summary>
/// <remarks>
/// The bike-workshop <c>service_orders.lines_count</c>, a count of <c>order_lines</c>. Its own world: it applies.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class IntegrityEditorWhereScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task IntegrityEditor_a_rollup_where_is_checked_live_and_saved()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var sheet = await IntegrityEditor.OpenFieldAsync(session, "service_orders", "lines_count");

        var box = sheet.GetByTestId("rollup-where");
        await box.FillAsync("missing == 'x'");
        var finding = session.Page.GetByTestId("check-rollup-where").First;
        await finding.WaitForAsync(new() { Timeout = 5_000 });
        (await finding.InnerTextAsync()).ShouldContain("'missing' is not a field of entity 'order_lines'");
        await box.FillAsync("quantity > 1");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 5_000 });

        await IntegrityEditor.SaveAndApplyAsync(session, "lines_count", "Count only multi-unit lines");

        var reopened = await IntegrityEditor.OpenFieldAsync(session, "service_orders", "lines_count");
        await Expect(reopened.GetByTestId("rollup-where")).ToHaveValueAsync("quantity > 1");

        session.AssertConsoleClean();
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}

/// <summary>The steps the integrity scenarios share: open a declared or a new field's sheet, and save, preview and apply it.</summary>
internal static class IntegrityEditor
{
    /// <summary>Opens the field editor on a declared field and answers its sheet.</summary>
    public static async Task<ILocator> OpenFieldAsync(AdminSession session, string entity, string field)
    {
        await session.GoAsync($"/schema/{entity}");
        await session.Page.GetByTestId($"edit-field-{field}").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.WaitForAsync();
        return sheet;
    }

    /// <summary>Opens the field editor on a new field of <paramref name="entity"/>, named <paramref name="field"/>, and answers its sheet.</summary>
    public static async Task<ILocator> NewFieldAsync(AdminSession session, string entity, string field)
    {
        await session.GoAsync($"/schema/{entity}");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync(field);
        return sheet;
    }

    /// <summary>Saves the open sheet to the working copy, previews it, and applies it with <paramref name="reason"/>.</summary>
    public static async Task SaveAndApplyAsync(AdminSession session, string field, string reason)
    {
        await session.Page.GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId($"field-row-{field}").WaitForAsync();
        await session.PreviewPendingAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        await session.Page.FillAsync("#apply-reason", reason);
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Apply these changes" }).ClickAsync();
        await session.Content.GetByText("Applied as revision").First.WaitForAsync();
    }
}
