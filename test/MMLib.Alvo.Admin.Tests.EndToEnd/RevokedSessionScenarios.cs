namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A session that outlives the account it names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by running the thing, not by reading it.</b> A cookie survives the database that issued
/// it, so a developer who deletes the SQLite file and restarts — which is the ordinary inner loop —
/// comes back to a dashboard that renders its whole chrome and refuses every screen. The refusal is
/// correct; what was missing was any way out of it, because the only sign-out control lives in the
/// sidebar on a desktop and behind the sheet on a phone.
/// </para>
/// <para>
/// This scenario reaches the same state the supported way — disabling the operator — and asserts
/// both halves: the refusal is rendered, and it carries the control that resolves it.
/// </para>
/// <para>
/// <b>Its own world, and it ends by locking that world out.</b> Disabling the bootstrap
/// administrator of a project whose <c>access</c> block admits nobody else leaves nobody who can
/// manage it, which is exactly why no other scenario may share this fixture.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RevokedSessionScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_session_whose_account_is_gone_is_offered_the_way_out()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await DisableTheOperatorAsync();

        await session.GoAsync("");
        await session.Page.GetByText("not allowed").First.WaitForAsync();

        var exit = session.Page.Locator("[data-testid='forbidden-sign-out']");
        (await exit.CountAsync()).ShouldBeGreaterThan(0, "a refusal with no control on the screen is a dead end");

        await exit.First.ClickAsync();
        await session.Page.WaitForURLAsync("**/admin/sign-in**");
    }

    /// <summary>
    /// Bars the signed-in administrator, through the store rather than through a screen.
    /// </summary>
    /// <remarks>
    /// The unguarded registration, because the guarded one asks who is calling and this call has no
    /// caller — it is the test standing in for the second administrator a real deployment would
    /// have.
    /// </remarks>
    private async Task DisableTheOperatorAsync()
    {
        using var scope = world.Services.CreateScope();
        var people = scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey);

        var page = await people.ListAsync(new AlvoUserQuery());
        var operatorAccount = page.Users.Single(
            person => string.Equals(person.Email, AdminWorld.AdminEmail, StringComparison.OrdinalIgnoreCase));

        await people.SetDisabledAsync(operatorAccount.Id, disabled: true);
    }
}
