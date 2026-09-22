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

        var text = await session.Page.Locator("main.a-content").InnerTextAsync();
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

        await session.Page.FillAsync("#new-person-email", "dispatcher@alvo.test");
        await session.Page.ClickAsync("button:has-text('Create')");

        /* Waiting for the row rather than settling and reading: the list re-renders over the
           circuit, and network-idle is true the whole time a WebSocket is quiet. */
        await session.Page.GetByText("dispatcher@alvo.test").First.WaitForAsync();

        /* Addressed by the person's own id rather than by a row that happens to contain their
           address: `.a-row` nests, so a text-scoped locator can match an ancestor whose "Change"
           button belongs to somebody else. */
        var person = await IdOfAsync(session, "dispatcher@alvo.test");
        await session.Page.ClickAsync($"#change-{person}");
        await session.Page.Locator($"#issue-token-{person}").WaitForAsync();
        await session.Page.ClickAsync($"#issue-token-{person}");

        /* Waiting on "out of band" rather than on "credential token": the latter is also the text
           of the button that was just clicked, so the wait would match the control instead of the
           panel and return before anything had happened. */
        await session.Page.GetByText("out of band").First.WaitForAsync();

        var text = await session.Page.Locator("main.a-content").InnerTextAsync();
        text.ShouldContain("Credential token");
        text.ShouldContain("out of band");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Nobody grants themselves a tenant, and the refusal names the recorded alternative.
    /// </summary>
    /// <remarks>
    /// The guard is in the core, not in this screen — the screen only has to render what the core
    /// refused. Asserting it here therefore measures both halves at once: that the guard fires, and
    /// that the operator is told what to do instead.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_administrator_cannot_grant_themselves_a_tenant()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        var me = await IdOfAsync(session, AdminWorld.AdminEmail);
        await session.Page.ClickAsync($"#change-{me}");
        await session.Page.Locator($"#tenant-{me}").WaitForAsync();
        await session.Page.FillAsync($"#tenant-{me}", "9f1c4a20-7d38-4a5e-9c11-2b6e0d4f8a4e");
        await session.Page.ClickAsync($"#grant-tenant-{me}");
        await session.Page.GetByText("higher management level").First.WaitForAsync();

        var text = await session.Page.Locator("main.a-content").InnerTextAsync();
        text.ShouldContain("higher management level");
        text.ShouldContain("Another administrator");
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

        var me = await IdOfAsync(session, AdminWorld.AdminEmail);
        await session.Page.ClickAsync($"#change-{me}");
        await session.Page.Locator($"#disable-{me}").WaitForAsync();
        await session.Page.ClickAsync($"#disable-{me}");
        await session.Page.GetByText("bootstrap administrator cannot be").First.WaitForAsync();
        session.AssertConsoleClean();
    }

    /// <summary>
    /// The id of the row whose text contains an address.
    /// </summary>
    /// <remarks>
    /// Read off the row's own <c>id</c> attribute, which the screen sets per person. The
    /// alternative — locating by text and hoping the right ancestor matches — is what this replaced
    /// after it clicked the wrong row's button.
    /// </remarks>
    /// <param name="session">The signed-in session.</param>
    /// <param name="email">Whose row.</param>
    /// <returns>The uuid in the row's id.</returns>
    private static async Task<string> IdOfAsync(AdminSession session, string email)
    {
        var id = await session.Page
            .Locator($".a-row[id^='person-']:has-text('{email}')").First
            .GetAttributeAsync("id");

        id.ShouldNotBeNull($"no person row carries {email}");
        return id["person-".Length..];
    }
}
