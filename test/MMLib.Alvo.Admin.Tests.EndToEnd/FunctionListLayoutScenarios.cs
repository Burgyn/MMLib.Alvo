using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Where the function list is drawn and how it fits (spec §9.1, §9.5): at phone width under both boxes — the document,
/// the sheet and every word — and absent where the box writes no CEL, asserted only after the list was shown in the same
/// session, so the absence cannot be a list that had not loaded yet (D-10).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class FunctionListLayoutScenarios(RecordingWorld world) : IClassFixture<RecordingWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_function_list_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 375);
        await FunctionOfferScenarios.NewMutateExpressionAsync(session, "rentals", "beforeUpdate", "damage_report");
        await FunctionOfferScenarios.OpenListAsync(session, "hook-mutate-value-0");
        await AssertFitsAsync(session);

        await FunctionOfferScenarios.TextModeAsync(session);
        await FunctionOfferScenarios.OpenListAsync(session, "hook-condition");
        await AssertFitsAsync(session);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Guided_rows_and_literal_values_offer_no_list()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await FunctionOfferScenarios.NewHookAsync(session, "parts", "beforeCreate", "mutate");

        await FunctionOfferScenarios.TextModeAsync(session);
        await FunctionOfferScenarios.OpenListAsync(session, "hook-condition");
        await FunctionOfferScenarios.ConditionModeAsync(session, "Guided");
        await GoneAsync(session, "fn-list-hook-condition", "guided mode writes its own CEL");

        await session.ChooseAsync(FunctionOfferScenarios.Combobox(session, "Field 1"), "name");
        await FunctionOfferScenarios.MutateModeAsync(session, 0, "an expression");
        await FunctionOfferScenarios.OpenListAsync(session, "hook-mutate-value-0");
        await FunctionOfferScenarios.MutateModeAsync(session, 0, "a value");
        await GoneAsync(session, "fn-list-hook-mutate-value-0", "a literal value is no CEL");
        session.AssertConsoleClean();
    }

    private static async Task AssertFitsAsync(AdminSession session)
    {
        await session.AssertNoHorizontalScrollAsync();
        await session.AssertSheetFitsAsync();
        await session.AssertNoVerticalTextAsync();
    }

    /// <summary>The list was on the page a moment ago; wait until it is detached rather than counting right after a click.</summary>
    private static async Task GoneAsync(AdminSession session, string testId, string because)
    {
        await session.Page.GetByTestId(testId).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId(testId).CountAsync()).ShouldBe(0, because);
    }
}
