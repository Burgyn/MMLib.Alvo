using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A person the dashboard creates sets a password from the credential token's link, and signs in with it
/// (todo item 30; design <c>2026-09-27-f5-admin-set-password-design.md</c> §1, §7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The person is a stranger to the administrator's browser.</b> Every link is opened in a fresh browser context,
/// with no cookie of the administrator's, because that is what handing a link over out of band means: the page and
/// its post are anonymous, and a scenario that opened the link in the signed-in context would pass on a page that
/// quietly leaned on the administrator's session.
/// </para>
/// <para>
/// <b>Its own world</b>: it creates people and changes their passwords. Each fact creates its own person, because
/// xUnit v3 runs a class's facts in no set order. The passwords are test values, set here, never a real one.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class SetPasswordScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>The first password each person sets: a test value, 15 characters or more, not their address.</summary>
    private const string FirstPassword = "Alvo-e2e-Person-Pass-1";

    /// <summary>The password set the second time, from a second link.</summary>
    private const string SecondPassword = "Alvo-e2e-Person-Pass-2";

    /// <summary>The declared role every person here is given, so the shell has one to show.</summary>
    private const string Role = "dispatcher";

    /// <summary>
    /// How long an open tab may keep its state after its password changed elsewhere: the circuit's revalidation
    /// interval (thirty seconds, <c>AlvoIdentityRevalidatingAuthenticationStateProvider.Interval</c>), plus room for
    /// the check itself and the redirect.
    /// </summary>
    private const int RevalidationWait = 45_000;

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_person_the_dashboard_creates_can_set_a_password_and_sign_in()
    {
        const string email = "set-password@alvo.test";
        var handover = await CreateAndIssueAsync(email);

        await SetPasswordFromLinkAsync(handover.Link, email, FirstPassword);

        await using var person = await world.SignInAsAsync(email, FirstPassword, TestContext.Current.CancellationToken);
        await AssertShellIsTheirsAsync(person, email);
        person.AssertConsoleClean();
    }

    /// <summary>
    /// A link works once: the second visit gets the one refusal every dead link gets, with no fragment carried back.
    /// </summary>
    /// <remarks>
    /// Single use comes from the security stamp the redemption rotated, not from a record of used tokens (design §3),
    /// so this is also the end-to-end check that a redemption rotates it.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_same_link_a_second_time_is_refused_the_generic_way()
    {
        const string email = "used-link@alvo.test";
        var handover = await CreateAndIssueAsync(email);
        await SetPasswordFromLinkAsync(handover.Link, email, FirstPassword);

        await using var context = await world.Browser.NewContextAsync();
        var page = await OpenLinkAsync(context, handover.Link, email);
        await SubmitPasswordAsync(page, SecondPassword);

        await page.WaitForURLAsync($"**{AlvoAdmin.SetPasswordPath}?failed=true");
        (await page.GetByTestId("error-title").InnerTextAsync()).ShouldContain("This link does not work.");
        (await page.EvaluateAsync<string>("() => window.location.hash")).ShouldBeEmpty("a refusal carries no link back");

        /* And the first password still stands: a refused second use moved nothing. */
        await using var person = await world.SignInAsAsync(email, FirstPassword, TestContext.Current.CancellationToken);
        await AssertShellIsTheirsAsync(person, email);
    }

    /// <summary>
    /// With JavaScript off the link cannot fill the form, so both boxes are on screen and the token is pasted by hand.
    /// </summary>
    /// <remarks>
    /// The page is an ordinary form post precisely so this works (design §1, "Why the fragment"). The bare token is
    /// the one the person editor shows under the link, for this case.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task With_JavaScript_off_pasting_the_token_by_hand_also_works()
    {
        const string email = "no-script@alvo.test";
        var handover = await CreateAndIssueAsync(email);

        await using (var context = await world.Browser.NewContextAsync(new() { JavaScriptEnabled = false }))
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.SetPasswordPath}");

            /* The boxes, not the <noscript> note: measured, the note is never drawn in Chromium with
               JavaScriptEnabled = false (script execution is turned off by the automation protocol, not by the
               browser's own setting a person with JavaScript off has), so waiting on it timed out. What this browser
               can say is that the token's box was not hidden. */
            var token = page.GetByLabel("Token", new() { Exact = true });
            (await token.IsVisibleAsync()).ShouldBeTrue("with no script to fill it, the token's box is on screen");

            await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
            await token.FillAsync(handover.Token);
            await SubmitPasswordAsync(page, FirstPassword);
            await AssertPasswordSetAsync(page);
        }

        await using var person = await world.SignInAsAsync(email, FirstPassword, TestContext.Current.CancellationToken);
        await AssertShellIsTheirsAsync(person, email);
    }

    /// <summary>
    /// A tab the person already has open drops to sign-in once their password is set somewhere else — within the
    /// circuit's revalidation interval, with no navigation of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A person can only have a tab once they have a password</b>, so the scenario sets one first, signs in, and then
    /// an administrator issues a second link that is redeemed in another browser — the reset a person who forgot
    /// their password gets. The redemption rotates the stamp, and the open circuit's next revalidation reads it.
    /// </para>
    /// <para>
    /// <b>The interval is the shipped thirty seconds, not a shortened one.</b> It is a constant of the identity
    /// package with no configuration seam, and adding one to make a test faster would be a knob a deployment could
    /// turn; this scenario pays the half minute instead. <b>No page load in the tab</b> between the change and the
    /// wait, which is the whole point: a page load re-checks the cookie and would pass without the circuit check.
    /// </para>
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_open_tab_drops_to_sign_in_once_the_password_is_set_elsewhere()
    {
        const string email = "open-tab@alvo.test";
        var first = await CreateAndIssueAsync(email);
        await SetPasswordFromLinkAsync(first.Link, email, FirstPassword);

        await using var person = await world.SignInAsAsync(email, FirstPassword, TestContext.Current.CancellationToken);
        await AssertShellIsTheirsAsync(person, email);

        var second = await IssueAsync(email);
        await SetPasswordFromLinkAsync(second.Link, email, SecondPassword);

        await person.Page.WaitForURLAsync($"**{AlvoAdmin.SignInPath}**", new() { Timeout = RevalidationWait });

        /* And the new password is the one that works now. */
        await using var again = await world.SignInAsAsync(email, SecondPassword, TestContext.Current.CancellationToken);
        await AssertShellIsTheirsAsync(again, email);
    }

    /// <summary>
    /// Creates <paramref name="email"/> with the <see cref="Role"/> role, as the administrator, and issues their first
    /// credential token.
    /// </summary>
    private async Task<Handover> CreateAndIssueAsync(string email)
    {
        await using var admin = await world.SignInAsync(TestContext.Current.CancellationToken);
        await admin.GoAsync("/access");
        var id = await admin.CreatePersonAsync(email);

        var editor = await admin.OpenPersonAsync(id);
        await editor.GetByRole(AriaRole.Button, new() { Name = Role, Exact = true }).ClickAsync();
        await editor.GetByRole(AriaRole.Button, new() { Name = Role, Exact = true, Pressed = true }).WaitForAsync();
        await editor.GetByTestId("person-save").ClickAsync();
        await admin.SnackbarAsync($"Saved {email}");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        return await IssueFromEditorAsync(admin, id);
    }

    /// <summary>Issues <paramref name="email"/> a further credential token, as the administrator, in a session of its own.</summary>
    private async Task<Handover> IssueAsync(string email)
    {
        await using var admin = await world.SignInAsync(TestContext.Current.CancellationToken);
        await admin.GoAsync("/access");
        return await IssueFromEditorAsync(admin, await admin.PersonIdAsync(email));
    }

    /// <summary>Opens the person's editor, issues a token, and reads the link and the bare token off the panel.</summary>
    private static async Task<Handover> IssueFromEditorAsync(AdminSession admin, string id)
    {
        var editor = await admin.OpenPersonAsync(id);
        await editor.GetByTestId("person-issue-token").ClickAsync();
        var link = editor.GetByTestId("person-token-link");
        await link.WaitForAsync();

        var handover = new Handover(
            (await link.InnerTextAsync()).Trim(),
            (await editor.GetByTestId("person-token").InnerTextAsync()).Trim());
        admin.AssertConsoleClean();
        return handover;
    }

    /// <summary>
    /// Opens the link in a browser of its own, sets <paramref name="password"/>, and lands on sign-in with its notice.
    /// </summary>
    private async Task SetPasswordFromLinkAsync(string link, string email, string password)
    {
        await using var context = await world.Browser.NewContextAsync();
        var page = await OpenLinkAsync(context, link, email);
        await SubmitPasswordAsync(page, password);
        await AssertPasswordSetAsync(page);
    }

    /// <summary>
    /// Opens the link and waits for the page to have taken the address and token out of the fragment — and the
    /// fragment out of the address bar, which is what keeps a bearer credential out of the history (design §1.3).
    /// </summary>
    private static async Task<IPage> OpenLinkAsync(IBrowserContext context, string link, string email)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(link);

        /* Polled rather than read once: the module that clears it is deferred, and a read at load could race it. */
        await page.WaitForFunctionAsync(
            "() => window.location.hash === ''", null, new PageWaitForFunctionOptions { PollingInterval = 100 });
        page.Url.ShouldNotContain("#", Case.Sensitive, "the address bar keeps the link's fragment after load");
        page.Url.ShouldNotContain("token", Case.Insensitive, "the token reached the address bar");

        var address = page.GetByLabel("Email", new() { Exact = true });
        (await address.InputValueAsync()).ShouldBe(email, "the link did not fill the address");
        (await address.GetAttributeAsync("readonly")).ShouldNotBeNull("a prefilled address is read-only");
        (await page.GetByLabel("Token", new() { Exact = true }).IsVisibleAsync())
            .ShouldBeFalse("a prefilled token is hidden");
        return page;
    }

    /// <summary>Types <paramref name="password"/> into both boxes and submits the form.</summary>
    private static async Task SubmitPasswordAsync(IPage page, string password)
    {
        await page.GetByLabel("New password", new() { Exact = true }).FillAsync(password);
        await page.GetByLabel("Repeat it", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Set password", Exact = true }).ClickAsync();
    }

    /// <summary>The post landed on sign-in, which says the password is set.</summary>
    private static async Task AssertPasswordSetAsync(IPage page)
    {
        await page.WaitForURLAsync($"**{AlvoAdmin.SignInPath}?passwordSet=true");
        (await page.GetByTestId("sign-in-password-set").InnerTextAsync())
            .ShouldContain("Your password is set. Sign in with it.");
    }

    /// <summary>The shell rendered for this person: the account menu names them and the role they were given.</summary>
    private static async Task AssertShellIsTheirsAsync(AdminSession person, string email)
    {
        person.Page.Url.ShouldEndWith(AlvoAdmin.BasePath);
        await person.Page.GetByTestId("account-menu").ClickAsync();
        await person.Page.GetByText(email, new() { Exact = true }).WaitForAsync();
        await person.Page.GetByText($"{Role} · ").WaitForAsync();
        await person.Page.Keyboard.PressAsync("Escape");
    }

    /// <summary>What the person editor hands over for one token: the link, and the bare token under it.</summary>
    /// <param name="Link">The set-password link.</param>
    /// <param name="Token">The token alone.</param>
    private sealed record Handover(string Link, string Token);
}
