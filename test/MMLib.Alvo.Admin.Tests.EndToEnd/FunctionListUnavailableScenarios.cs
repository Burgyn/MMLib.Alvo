using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A deployment whose function list cannot be read: no list is offered, and the editor still works (spec §9.3).</summary>
public sealed class FunctionsRefusedWorld : RecordingWorld
{
    /// <inheritdoc/>
    protected override Task<ManagementCelFunctions> ListFunctionsAsync(Func<Task<ManagementCelFunctions>> shipped) =>
        throw new ManagementForbiddenException();
}

/// <summary>
/// When <c>cel/functions</c> is refused the hook editor offers no list and still adds the hook. The absence is asserted
/// only after the world recorded the refused request, so it cannot be a list that had not loaded yet (D-10).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class FunctionListUnavailableScenarios(FunctionsRefusedWorld world) : IClassFixture<FunctionsRefusedWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_function_list_leaves_the_editor_working()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var seen = world.FunctionListsAnswered;
        await FunctionOfferScenarios.NewMutateExpressionAsync(session, "parts", "beforeCreate", "name");
        await world.FunctionListAnsweredAsync(seen);

        await session.Page.FillAsync("#hook-mutate-value-0", "trim(new.name)");
        (await world.CheckedAsync("trim(new.name)")).Findings.ShouldBeEmpty("the check still works without the list");
        (await session.Page.GetByTestId("fn-list-hook-mutate-value-0").CountAsync()).ShouldBe(0, "the refused request was answered, and nothing was drawn");

        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

        /* The sheet closing proves only that it closed: the hook must be on the On write tab, staged, with its expression. */
        var row = session.Page.Locator("#hook-beforeCreate-0");
        await row.WaitForAsync();
        (await row.GetByTestId("hook-staged").CountAsync()).ShouldBe(1, "the hook just added is the staged one");
        (await row.InnerTextAsync()).ShouldContain("trim(new.name)");
        session.AssertConsoleClean();
    }
}
