namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The Fields tab shows what the descriptor declares of each field: its <c>hidden</c> and <c>readOnly</c> policy
/// (#267's read half) and its description (#268). Read-only: nothing is staged.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class DeclaredFieldScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>field-service declares <c>internal_notes</c> hidden and <c>external_ref</c> read-only.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hidden_and_a_read_only_field_say_so_on_their_rows()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        var hidden = session.Page.GetByTestId("field-row-internal_notes");
        await hidden.WaitForAsync();
        (await hidden.InnerTextAsync()).ShouldContain("hidden");
        (await session.Page.GetByTestId("field-row-external_ref").InnerTextAsync()).ShouldContain("readOnly");
        (await session.Page.GetByTestId("field-row-title").InnerTextAsync()).ShouldNotContain("hidden");

        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_field_row_shows_its_description()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        var description = session.Page.GetByTestId("field-description-title");
        await description.WaitForAsync();
        (await description.InnerTextAsync()).ShouldBe("One line describing the job.");

        session.AssertConsoleClean();
    }
}
