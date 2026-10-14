using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The field editor's <em>Exclude from event images</em> (spec 2026-10-14 HX-01 acceptance 8), and what it does to the hook editor.</summary>
/// <remarks>Its own world, because it applies.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class EventExclusionScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private const string Toggle = "exclude from event images";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Excluding_a_field_from_event_images_persists_through_an_apply()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await ExcludeAsync(session, "name");

        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", "Keep region names out of events");
        await session.Button("Apply these changes").ClickAsync();
        await session.SnackbarAsync("Applied as revision");

        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("edit-field-name").ClickAsync();
        (await ToggleIn(session).IsCheckedAsync())
            .ShouldBeTrue("the applied descriptor's events.exclude must be read back into the editor");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// After the commit a hook reads the event's images, which leave the field out — apply refuses the read — so the guided
    /// condition offers only <c>changed</c>, which names it without reading it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_excluded_field_is_offered_only_changed_after_the_commit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await ExcludeAsync(session, "code");

        await session.OpenTabAsync(HookEditInPlaceScenarios.OnWriteTab);
        await GuidedConditionScenarios.OpenNewAsync(session);
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = "afterUpdate", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(GuidedConditionScenarios.Combobox(session, "Condition 1 field"), "code");

        await GuidedConditionScenarios.Combobox(session, "Condition 1 operator").ClickAsync();
        var options = session.Page.GetByRole(AriaRole.Option);
        await options.First.WaitForAsync();
        (await options.AllInnerTextsAsync()).Select(text => text.Trim()).ShouldBe(["changed"]);
        session.AssertConsoleClean();
    }

    private static async Task ExcludeAsync(AdminSession session, string field)
    {
        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId($"edit-field-{field}").ClickAsync();
        await ToggleIn(session).CheckAsync();
        await session.Page.GetByTestId("field-save").ClickAsync();
    }

    private static ILocator ToggleIn(AdminSession session)
        => session.Page.GetByTestId("field-sheet").GetByRole(AriaRole.Checkbox, new() { Name = Toggle, Exact = true });
}
