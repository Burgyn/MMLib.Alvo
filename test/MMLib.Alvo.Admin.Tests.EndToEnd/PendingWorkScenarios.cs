namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The shell says what is waiting for an apply, and the tab says what was staged on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two failed usability tasks, measured rather than remembered</b> (design pass §3): from Data, an operator
/// could not tell whether anything was waiting to be applied (T4), and a field added to an entity did not
/// appear on the tab it was added on (D-9) — the only hint was a green button on one screen.
/// </para>
/// <para>
/// <b>Its own world for <c>IndexEditingScenarios</c>' reason</b>: a working copy is composed per operator, so
/// these share one with each other and with nobody else. Nothing here asserts an exact count for that reason.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class PendingWorkScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// A field staged on an entity is on its tab, badged, and the shell carries the count to every screen.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_staged_field_is_visible_on_its_tab_and_the_bar_follows_to_Data()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.ClickAsync("[data-testid='add-field']");
        await session.Page.Locator("[data-testid='field-sheet']").WaitForAsync();
        await session.Page.FillAsync("#new-field-name", "dispatch_zone");
        await session.Page.ClickAsync("[data-testid='field-save']");

        /* It stays on the entity: the bar is the way to Preview now, not a jump after every field. */
        await session.Page.Locator("[data-testid='staged-dispatch_zone']").WaitForAsync();
        session.Page.Url.ShouldEndWith("/schema/regions");
        (await session.Page.Locator("[data-testid='staged-dispatch_zone']").InnerTextAsync()).ShouldBe("new");

        await session.GoAsync("/data");
        await session.Page.Locator("[data-testid='pending-bar']").WaitForAsync();
        (await session.Page.Locator("[data-testid='pending-bar']").InnerTextAsync())
            .ShouldContain("unapplied change");
        (await session.Page.Locator("[data-testid='project-pending']").InnerTextAsync())
            .ShouldContain("unapplied");

        /* And not on Preview, where the same count is the whole page. */
        await session.PreviewPendingAsync();
        (await session.Page.Locator("[data-testid='pending-bar']").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Discarding from the bar takes the staged row off the tab behind it, and the bar with it.
    /// </summary>
    /// <remarks>
    /// The page under the sheet never learns the bar exists: the bar re-navigates to the same address, and the
    /// tab re-reads the copy. Asserted on the tab, because a bar that went away over a Fields tab still listing
    /// the field would be the two halves of the shell disagreeing about one copy.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Confirming_discard_from_the_bar_clears_the_tab_and_the_bar()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.ClickAsync("[data-testid='add-field']");
        await session.Page.Locator("[data-testid='field-sheet']").WaitForAsync();
        await session.Page.FillAsync("#new-field-name", "short_lived");
        await session.Page.ClickAsync("[data-testid='field-save']");
        await session.Page.Locator("[data-testid='staged-short_lived']").WaitForAsync();

        await session.Page.ClickAsync("[data-testid='pending-discard']");
        await session.Page.ClickAsync("[data-testid='discard-confirm']");

        var detached = new Microsoft.Playwright.LocatorWaitForOptions
        {
            State = Microsoft.Playwright.WaitForSelectorState.Detached,
        };
        await session.Page.Locator("[data-testid='field-row-short_lived']").WaitForAsync(detached);
        await session.Page.Locator("[data-testid='pending-bar']").WaitForAsync(detached);
        (await session.Page.Locator("[data-testid='project-pending']").CountAsync()).ShouldBe(0);
        session.Page.Url.ShouldEndWith("/schema/regions");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Discarding looks destructive and can be backed out of; a removed field stays, struck through, and
    /// Undo puts it back.
    /// </summary>
    /// <remarks>
    /// D-7: the confirmation used to be the primary green of every safe action, with no Cancel beside it.
    /// Undo is asserted by the row becoming an ordinary one again rather than by the bar going away, because
    /// the other scenario here may have left its own field staged in the shared copy. That the restore leaves
    /// no diff behind is <c>WorkingCopyPendingTests</c>' to pin.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Discard_is_danger_and_cancellable_and_a_removed_field_can_be_undone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.ClickAsync("[data-testid='remove-field-notes']");
        await session.Page.Locator("[data-testid='staged-notes']").WaitForAsync();
        (await session.Page.Locator("[data-testid='staged-notes']").InnerTextAsync()).ShouldBe("removed");

        await session.Page.ClickAsync("[data-testid='pending-discard']");
        var confirm = session.Page.Locator("[data-testid='discard-confirm']");
        await confirm.WaitForAsync();
        (await confirm.GetAttributeAsync("class") ?? string.Empty).ShouldContain("a-btn--danger");

        await session.Page.ClickAsync("[data-testid='discard-cancel']");
        await session.Page.Locator("[data-testid='discard-sheet']").WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

        /* Cancelling is not confirming: the removal is still staged. */
        await session.Page.Locator("[data-testid='staged-notes']").WaitForAsync();
        await session.Page.Locator("[data-testid='pending-bar']").WaitForAsync();

        await session.Page.ClickAsync("[data-testid='restore-field-notes']");
        await session.Page.Locator("[data-testid='staged-notes']").WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        await session.Page.Locator("[data-testid='remove-field-notes']").WaitForAsync();

        session.AssertConsoleClean();
    }
}
