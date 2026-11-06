using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// HX-12's dashboard slice: a <c>sequence</c> is chosen in the field editor as a third maintained kind, its format checked
/// and previewed as it is typed, applied, and the first create is numbered by the framework.
/// </summary>
/// <remarks>
/// A new string field <c>parts.number</c> counted per <c>category</c> — a required enum the per picker offers. Its own
/// world: it applies.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class IntegrityEditorSequenceScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task IntegrityEditor_a_sequence_is_saved_applied_and_numbers_a_record()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var sheet = await IntegrityEditor.NewFieldAsync(session, "parts", "number");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "sequence", Exact = true }).ClickAsync();
        await sheet.GetByTestId("sequence-per").WaitForAsync();
        var asString = sheet.GetByRole(AriaRole.Radio, new() { Name = "string", Exact = true });
        await asString.ClickAsync();
        await Assertions.Expect(asString).ToHaveAttributeAsync("aria-checked", "true");
        await sheet.GetByTestId("sequence-per").GetByRole(AriaRole.Radio, new() { Name = "category", Exact = true }).ClickAsync();

        var format = sheet.GetByTestId("sequence-format");
        await format.FillAsync("P-{n}-{n}");
        var problem = sheet.GetByTestId("check-sequence-format");
        await problem.WaitForAsync(new() { Timeout = 5_000 });
        (await problem.InnerTextAsync()).ShouldContain("exactly one {n}");
        await format.FillAsync("P-{n:3}");
        await problem.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 5_000 });
        await Assertions.Expect(sheet.GetByTestId("sequence-preview")).ToContainTextAsync("P-001");

        await IntegrityEditor.SaveAndApplyAsync(session, "number", "Number the parts per shelf");
        var reopened = await IntegrityEditor.OpenFieldAsync(session, "parts", "number");
        await Assertions.Expect(reopened.GetByTestId("sequence-format")).ToHaveValueAsync("P-{n:3}");

        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var created = await data.CreateAsync(
            "parts", Part(), AlvoContext.System(tenant: null), cancellationToken: TestContext.Current.CancellationToken);
        var stored = await data.GetAsync("parts", (Guid)created["id"]!, StaffReader, TestContext.Current.CancellationToken);
        stored.ShouldNotBeNull()["number"].ShouldBe("P-001");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A reader the parts rules admit — they ask for <c>authenticated</c>, which the system identity does not hold, so the
    /// create's own answer carries the id alone.
    /// </summary>
    private static AlvoContext StaffReader { get; } = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Admin, Role.Authenticated },
    };

    private static Dictionary<string, object?> Part() => new(StringComparer.Ordinal)
    {
        ["sku"] = "SHI-SEQ-001",
        ["name"] = "A chain numbered by the framework",
        ["brand"] = "Shimano",
        ["category"] = "drivetrain",
        ["unit_price"] = 20m,
        ["purchase_price"] = 12m,
    };
}
