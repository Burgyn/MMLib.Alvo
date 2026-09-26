namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Saving the AI connection is a snackbar, not a word left beside the button (spec §3.3; inventory §2d.3).</summary>
/// <param name="world">A host with an agent installed and a writable secret store.</param>
public sealed class SettingsScenarios(ConfigurableAssistantWorld world) : IClassFixture<ConfigurableAssistantWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Saving_the_connection_says_so_once_and_leaves_nothing_behind()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        await session.Page.FillAsync("#ai-endpoint", "http://127.0.0.1:1/v1");
        await session.Page.FillAsync("#ai-model", "scripted");
        await session.Page.GetByTestId("ai-save").ClickAsync();

        await session.SnackbarAsync("Saved the AI connection");
        (await session.SnackbarCountAsync("Saved the AI connection")).ShouldBe(1, "one save, said once");
        (await session.Content.GetByText("Saved.", new() { Exact = true }).CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    /// <summary>
    /// An endpoint that is not an address is refused under its field, which takes focus, and nothing says it saved.
    /// </summary>
    /// <remarks>
    /// The store takes any text, and the resolver then reads a connection it cannot build as none at all: the
    /// screen would say saved and the status would stay "not configured", with nothing saying why.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_endpoint_that_is_not_an_address_is_refused_under_its_field()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        await session.Page.FillAsync("#ai-endpoint", "localhost");
        await session.Page.FillAsync("#ai-model", "scripted");
        await session.Page.GetByTestId("ai-save").ClickAsync();

        await session.Content.GetByTestId("field-problem").WaitForAsync();
        (await session.FocusedAsync()).ShouldStartWith("input#ai-endpoint");
        (await session.Page.Locator("#ai-endpoint").GetAttributeAsync("aria-invalid")).ShouldBe("true");
        (await session.SnackbarCountAsync()).ShouldBe(0, "a refused save never says it saved");
        session.AssertConsoleClean();
    }
}
