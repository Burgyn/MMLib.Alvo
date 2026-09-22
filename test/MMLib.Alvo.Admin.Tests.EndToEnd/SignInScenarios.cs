using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Getting in, and being kept out.
/// </summary>
/// <remarks>
/// The first scenario of a first session, and the one every other scenario depends on — so it is
/// also where the two things that used to break silently are asserted: the framework's script is
/// served (without it the page renders once and never becomes interactive, with a 404 nobody
/// reads), and the design system is served (without it the screen is unstyled and every layout
/// assertion below is meaningless).
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class SignInScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_unauthenticated_visitor_is_sent_to_the_sign_in_screen()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        await using var context = await world.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.BasePath}");

        page.Url.ShouldContain(AlvoAdmin.SignInPath);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_wrong_password_says_so_and_does_not_say_which_half_was_wrong()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        await using var context = await world.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.SignInPath}");
        await page.FillAsync("#email", AdminWorld.AdminEmail);
        await page.FillAsync("#password", "not-the-password");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync($"**{AlvoAdmin.SignInPath}?**");

        var message = await page.Locator(".a-error__title").InnerTextAsync();
        message.ShouldContain("do not match");
        message.ShouldNotContain("password is", Case.Insensitive);
        message.ShouldNotContain("no such", Case.Insensitive);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_address_that_has_no_account_is_refused_the_same_way()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        await using var context = await world.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.SignInPath}");
        await page.FillAsync("#email", "nobody@alvo.test");
        await page.FillAsync("#password", AdminWorld.AdminPassword);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync($"**{AlvoAdmin.SignInPath}?**");

        (await page.Locator(".a-error__title").InnerTextAsync()).ShouldContain("do not match");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_bootstrap_administrator_signs_in_and_lands_on_the_project()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);

        session.Page.Url.ShouldEndWith(AlvoAdmin.BasePath);
        (await session.Page.Locator("h1").First.InnerTextAsync()).ShouldBe("field-service");
        await session.AssertRenderedAsync();
        session.AssertConsoleClean();
    }

    /// <summary>
    /// The framework's script is served, so the page becomes interactive.
    /// </summary>
    /// <remarks>
    /// This is a regression test with a name: the Web SDK decides a project needs Blazor's script
    /// by looking for <c>.razor</c> files <em>in that project</em>, and every component here lives
    /// in another assembly by design. The heuristic answered "no Blazor", the script was left out
    /// of the manifest, and nothing failed at build time — the only symptom was a 404 in a console
    /// nobody was reading.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_blazor_script_and_the_design_system_are_both_served()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        await using var context = await world.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var served = new List<(string Asset, int Status, int Length)>();
        foreach (var asset in new[]
        {
            "/_framework/blazor.web.js",
            $"/{AlvoAdminAssets.StyleSheet}",
            $"/{AlvoAdminAssets.Script}",
            AlvoAdminAssets.Module,
        })
        {
            var response = await page.APIRequest.GetAsync($"{world.BaseAddress}{asset}");
            var body = await response.TextAsync();
            served.Add((asset, response.Status, body.Length));
        }

        /* The length matters as much as the status, and that is not belt and braces. A static-asset
           manifest whose file provider cannot resolve the files answers 200 with an EMPTY body — so
           the page loads, renders unstyled and inert, and a suite that only checked the status goes
           green while measuring a broken application. That is exactly what happened here once. */
        /* The length is compared as a number. The first version of this assertion asked whether the
           entry's text ended with "0 bytes", which is true of an empty body and equally true of a
           stylesheet that happens to be 28 590 bytes long — so growing the design system by one rule
           failed a test about a file provider. */
        var report = string.Join("; ", served.Select(e => $"{e.Asset} -> {e.Status}, {e.Length} bytes"));
        served.ShouldAllBe(entry => entry.Status == 200 && entry.Length > 0, report);
    }

    /// <summary>
    /// Signing out clears the session, and cannot be done by a link somebody else embeds.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Signing_out_is_a_post_and_a_get_does_nothing()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);

        /* The claim is "a GET does not sign you out", not "a GET answers 405": the route is
           POST-only, and whether ASP.NET answers 405 or the component router's catch-all answers
           404 is a framework detail that would make this test break for the wrong reason. What
           must hold is that the session survives it — which the navigation below measures. */
        var byGet = await session.Page.APIRequest.GetAsync(
            $"{world.BaseAddress}{AlvoAdmin.SignOutEndpoint}");
        byGet.Ok.ShouldBeFalse("a GET to the sign-out endpoint was accepted");

        await session.Page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.BasePath}");
        session.Page.Url.ShouldEndWith(AlvoAdmin.BasePath);

        await session.Page.ClickAsync("button:has-text('Sign out')");
        await session.Page.WaitForURLAsync($"**{AlvoAdmin.SignInPath}");

        await session.Page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.BasePath}");
        session.Page.Url.ShouldContain(AlvoAdmin.SignInPath);
    }
}
