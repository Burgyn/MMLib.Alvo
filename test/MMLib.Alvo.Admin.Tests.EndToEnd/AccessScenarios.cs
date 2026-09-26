using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Administering the people who can reach the project.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class AccessScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_screen_separates_what_takes_effect_now_from_what_waits_for_an_apply()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        var text = await session.Content.InnerTextAsync();
        text.ShouldContain("Membership");
        text.ShouldContain("waits for an apply");
        text.ShouldContain(AdminWorld.AdminEmail);
        session.AssertConsoleClean();
    }

    /// <summary>
    /// A second person can be created, and is created without a password.
    /// </summary>
    /// <remarks>
    /// The absence of a password field is the point: a credential that travels as a value is
    /// readable by whoever handles it, which is the same reason this repository refuses a bootstrap
    /// password supplied as configuration.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_second_person_can_be_created_and_gets_a_credential_token()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        (await session.Page.Locator("input[type=password]").CountAsync())
            .ShouldBe(0, "the create form asks for a password");

        await session.CreatePersonAsync("dispatcher@alvo.test");

        /* Addressed by the person's own id rather than by a row that happens to contain their
           address: rows nest inside the panel, so a text-scoped locator can match an ancestor whose
           "Change" button belongs to somebody else. */
        var person = await session.PersonIdAsync("dispatcher@alvo.test");
        var editor = await session.OpenPersonAsync(person);
        await editor.GetByTestId("person-issue-token").ClickAsync();

        /* Waiting on "out of band" rather than on "credential token": the latter is also the text
           of the button that was just clicked, so the wait would match the control instead of the
           panel and return before anything had happened. */
        await editor.GetByText("out of band").First.WaitForAsync();

        var text = await editor.InnerTextAsync();
        text.ShouldContain("Credential token");
        text.ShouldContain("out of band");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Nobody grants themselves a tenant, and their own editor says so instead of offering the control.
    /// </summary>
    /// <remarks>
    /// The guard is in the core, not in this screen (§3.7 U3.2). The screen used to offer Grant on the
    /// operator's own row and then render the core's refusal at the top of the page (D-10); it now offers
    /// no tenant box and no Remove there and says, beside the tenant, who can make the change. Another
    /// person's editor still carries both, which is what keeps this from passing on a screen that dropped
    /// them for everybody.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_administrator_is_not_offered_a_tenant_for_themselves()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        var me = await session.PersonIdAsync(AdminWorld.AdminEmail);
        var mine = await session.OpenPersonAsync(me);
        await mine.GetByTestId("tenant-self").WaitForAsync();

        (await session.Page.Locator($"#tenant-{me}").CountAsync()).ShouldBe(0);
        (await session.Page.Locator($"#clear-tenant-{me}").CountAsync()).ShouldBe(0);
        (await mine.GetByTestId("tenant-self").InnerTextAsync())
            .ShouldContain("You cannot grant yourself a tenant — another administrator can.");

        /* The administrator holds only the built-in `admin` role, so a declared role is known to be
           unassigned in their editor. */
        (await mine.GetByRole(AriaRole.Button, new() { Name = "dispatcher", Exact = true })
            .GetAttributeAsync("aria-pressed")).ShouldBe("false");
        await mine.GetByTestId("editor-cancel").ClickAsync();
        await mine.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        // --- another person's editor keeps both tenant controls, and a role pressed there is assigned on Save
        await session.CreatePersonAsync("tenant-peer@alvo.test");

        var peer = await session.PersonIdAsync("tenant-peer@alvo.test");
        var theirs = await session.OpenPersonAsync(peer);

        (await session.Page.Locator($"#tenant-{peer}").CountAsync()).ShouldBe(1);
        (await session.Page.Locator($"#clear-tenant-{peer}").CountAsync()).ShouldBe(1);

        var dispatcher = theirs.GetByRole(AriaRole.Button, new() { Name = "dispatcher", Exact = true });
        (await dispatcher.GetAttributeAsync("aria-pressed")).ShouldBe("false");
        await dispatcher.ClickAsync();
        await theirs.GetByRole(AriaRole.Button, new() { Name = "dispatcher", Exact = true, Pressed = true }).WaitForAsync();
        await theirs.GetByTestId("person-save").ClickAsync();

        await session.SnackbarAsync("Saved tenant-peer@alvo.test");
        await session.Page.Locator($"#person-{peer}").Filter(new() { HasText = "dispatcher" }).WaitForAsync();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The bootstrap administrator cannot be disabled.
    /// </summary>
    /// <remarks>
    /// Traced rather than assumed: the context resolver answers nothing for a locked-out account
    /// before the bootstrap branch is reached, and the seed does not reset an existing row — so a
    /// project whose access block admits nobody else would be locked out of its own management
    /// surface with no way back but editing the identity database by hand.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_bootstrap_administrator_cannot_be_disabled()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        var me = await session.PersonIdAsync(AdminWorld.AdminEmail);
        var editor = await session.OpenPersonAsync(me);
        await editor.GetByTestId("person-disable").ClickAsync();
        await session.Dialog("disable-person").GetByTestId("disable-person-run").ClickAsync();
        await session.Content.GetByText("bootstrap administrator cannot be").First.WaitForAsync();
        session.AssertConsoleClean();
    }
}
