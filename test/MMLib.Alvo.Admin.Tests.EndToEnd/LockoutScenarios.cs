using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using System.Globalization;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A person locked out by failed sign-ins is shown as locked on Access, until when in the operator's own time, and an
/// administrator can end it with Unlock, after which they sign in (docs/todo-admin.md §8d items 39 and 46(i)).
/// </summary>
/// <remarks>
/// <b>Its own world</b>: it creates a person, gives them a password from a credential token's link and locks them out.
/// The passwords are test values set here, never a real one.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class LockoutScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>The password the person sets: a test value, 15 characters or more, not their address.</summary>
    private const string Password = "Alvo-e2e-Locked-Pass-1";

    /// <summary>How many wrong passwords lock an account: ASP.NET Core Identity's default, which the build keeps.</summary>
    private const int FailuresToLock = 5;

    /// <summary>The declared role the person is given.</summary>
    private const string Role = "dispatcher";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_person_locked_by_failed_sign_ins_shows_it_and_Unlock_lets_them_sign_in()
    {
        const string email = "locked-out@alvo.test";
        var id = await CreateWithPasswordAsync(email);

        await FailToSignInAsync(email, FailuresToLock);

        await using var admin = await world.SignInAsync(TestContext.Current.CancellationToken);
        await admin.GoAsync("/access");
        var said = await ExpectedWordsAsync(admin, email);

        /* Waited for by its exact words: until the circuit has asked the browser its offset the row says UTC. */
        await admin.Page.Locator($"#person-{id}").GetByTestId("person-locked")
            .GetByText(said, new() { Exact = true }).WaitForAsync();
        var editor = await admin.OpenPersonAsync(id);
        (await editor.GetByTestId("person-lockout").InnerTextAsync()).ShouldContain(said, Case.Sensitive);
        await editor.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await UnlockAsync(admin, id);

        (await admin.Page.Locator($"#person-{id}").GetByTestId("person-locked").CountAsync())
            .ShouldBe(0, "the row still says the person is locked after Unlock");
        await using var person = await world.SignInAsAsync(email, Password, TestContext.Current.CancellationToken);
        person.Page.Url.ShouldEndWith(AlvoAdmin.BasePath);
        admin.AssertConsoleClean();
    }

    /// <summary>
    /// Unlock leaves the editor for its confirm; Cancel keeps the lockout and gives focus to the row's Edit, and the verb
    /// ends it, says so once the list is read again, and does the same (spec §3.2).
    /// </summary>
    private static async Task UnlockAsync(AdminSession admin, string id)
    {
        var change = $"#change-{id}";
        await (await admin.OpenPersonAsync(id)).GetByTestId("person-unlock").ClickAsync();
        await admin.Dialog("unlock-person").GetByTestId("unlock-person-cancel").ClickAsync();
        await admin.FocusAfterConfirmAsync("unlock-person", change);
        (await admin.Page.Locator($"#person-{id}").GetByTestId("person-locked").CountAsync())
            .ShouldBe(1, "Cancel ended the lockout");

        await (await admin.OpenPersonAsync(id)).GetByTestId("person-unlock").ClickAsync();
        var confirm = admin.Dialog("unlock-person");
        (await confirm.InnerTextAsync()).ShouldContain("They can try to sign in again now.", Case.Sensitive);
        await confirm.GetByTestId("unlock-person-run").ClickAsync();
        await admin.SnackbarAsync("locked-out@alvo.test can try to sign in again");
        await admin.FocusAfterConfirmAsync("unlock-person", change);
    }

    /// <summary>
    /// "Locked until HH:mm after failed sign-ins", with the lockout's end as the store holds it, in the browser's own
    /// zone: the words the dashboard draws have to agree with both.
    /// </summary>
    /// <remarks>
    /// <b>The dashboard's own clock rule, restated</b> (<c>OperatorTime.Clock</c>, internal to the dashboard): the date
    /// is drawn too when the lockout ends on another local day than today, so a run just before midnight — whose
    /// five-minute lockout ends after it — expects <c>yyyy-MM-dd HH:mm</c>, not a bare <c>HH:mm</c> the row never says.
    /// </remarks>
    private async Task<string> ExpectedWordsAsync(AdminSession admin, string email)
    {
        using var scope = world.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IAlvoUserStore>()
            .FindByEmailAsync(email, TestContext.Current.CancellationToken);
        var until = stored.ShouldNotBeNull().LockedOutUntil.ShouldNotBeNull("five wrong passwords did not lock them");
        var east = await admin.Page.EvaluateAsync<int>("() => -new Date().getTimezoneOffset()");
        var zone = TimeSpan.FromMinutes(east);
        var local = until.ToOffset(zone);
        var today = DateTimeOffset.UtcNow.ToOffset(zone).Date == local.Date;
        var clock = local.ToString(today ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return $"Locked until {clock} after failed sign-ins";
    }

    /// <summary>Signs in as <paramref name="email"/> with a wrong password <paramref name="times"/> times, from a stranger's browser.</summary>
    private async Task FailToSignInAsync(string email, int times)
    {
        await using var context = await world.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        for (var attempt = 0; attempt < times; attempt++)
        {
            await page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.SignInPath}");
            await page.FillAsync("#email", email);
            await page.FillAsync("#password", "not-their-password");
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync($"**{AlvoAdmin.SignInPath}?**");
        }
    }

    /// <summary>
    /// Creates <paramref name="email"/> and gives them <see cref="Password"/> from a credential token's link, in an
    /// administrator's session of its own that ends with the token shown (the editor would ask before losing it).
    /// </summary>
    /// <returns>The person's id.</returns>
    private async Task<string> CreateWithPasswordAsync(string email)
    {
        string id, link;
        await using (var admin = await world.SignInAsync(TestContext.Current.CancellationToken))
        {
            await admin.GoAsync("/access");
            id = await admin.CreatePersonAsync(email);

            /* A declared role, as SetPasswordScenarios gives its people, so signing in lands on a shell of theirs. */
            var editor = await admin.OpenPersonAsync(id);
            await editor.GetByRole(AriaRole.Button, new() { Name = Role, Exact = true }).ClickAsync();
            await editor.GetByRole(AriaRole.Button, new() { Name = Role, Exact = true, Pressed = true }).WaitForAsync();
            await editor.GetByTestId("person-save").ClickAsync();
            await admin.SnackbarAsync($"Saved {email}");
            await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

            editor = await admin.OpenPersonAsync(id);
            await editor.GetByTestId("person-issue-token").ClickAsync();
            var shown = editor.GetByTestId("person-token-link");
            await shown.WaitForAsync();
            link = (await shown.InnerTextAsync()).Trim();
            admin.AssertConsoleClean();
        }

        await SetPasswordAsync(link, Password);
        return id;
    }

    /// <summary>Opens the link in a browser of its own and sets <paramref name="password"/>.</summary>
    private async Task SetPasswordAsync(string link, string password)
    {
        await using var context = await world.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(link);
        await page.WaitForFunctionAsync(
            "() => window.location.hash === ''", null, new PageWaitForFunctionOptions { PollingInterval = 100 });
        await page.GetByLabel("New password", new() { Exact = true }).FillAsync(password);
        await page.GetByLabel("Repeat it", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Set password", Exact = true }).ClickAsync();
        await page.WaitForURLAsync($"**{AlvoAdmin.SignInPath}?passwordSet=true");
    }
}
