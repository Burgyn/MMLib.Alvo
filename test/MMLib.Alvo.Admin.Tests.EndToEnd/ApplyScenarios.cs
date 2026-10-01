using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using System.Security.Claims;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>An apply cannot run twice, and says what it did twice: briefly, and where it stays (spec §3.3, §3.4).</summary>
/// <remarks>Its own world, because it applies; one apply per world (see ChangeTheBackendScenarios).</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ApplyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_apply_appends_one_revision_and_says_so()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var before = await ApplyScenarioSteps.RevisionCountAsync(session);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "tickets");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForURLAsync("**/schema/tickets");
        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", "Add tickets");

        await session.Button("Apply these changes").DblClickAsync();

        await session.SnackbarAsync("Applied as revision");
        await session.Content.GetByText("Applied as revision").WaitForAsync();
        (await session.Content.GetByRole(AriaRole.Link, new() { Name = "Open Configuration history" }).CountAsync())
            .ShouldBe(1, "the result stays, because it links onward");
        /* The one success in place is AlvoAlert's Success tone, a status (final review M8), not a hand-drawn panel. */
        (await session.Page.GetByTestId("apply-applied").GetAttributeAsync("role")).ShouldBe("status");
        (await ApplyScenarioSteps.RevisionCountAsync(session)).ShouldBe(before + 1);
        session.AssertConsoleClean();
    }
}

/// <summary>A destructive apply confirmed twice in a row applies once (spec §3.2, §3.4).</summary>
/// <remarks>Its own world, because it applies, and what it applies is a drop no other class may find already made.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class DestructiveApplyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_confirm_of_a_destructive_apply_appends_one_revision()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var before = await ApplyScenarioSteps.RevisionCountAsync(session);
        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("remove-field-code").ClickAsync();
        await session.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();
        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", "Drop the region code");

        await session.Button("Apply these changes").ClickAsync();
        var confirm = session.Dialog("apply-confirm");
        await confirm.WaitForAsync();
        await session.WaitForFocusOnAsync("confirm-name");
        await session.Page.Keyboard.TypeAsync("field-service");
        await session.Page.WaitForFunctionAsync(
            "() => !document.querySelector(\"[data-testid='apply-confirm-run']\")?.disabled");
        await confirm.GetByTestId("apply-confirm-run").DblClickAsync();

        await session.SnackbarAsync("Applied as revision");
        await session.Content.GetByText("Applied as revision").WaitForAsync();
        (await ApplyScenarioSteps.RevisionCountAsync(session)).ShouldBe(before + 1);
        session.AssertConsoleClean();
    }
}

/// <summary>A refused apply is an alert that takes focus, on every attempt, and never a snackbar (spec §3.3).</summary>
/// <remarks>
/// Its own world, because it applies behind the dashboard's back: the refusal is the one the page itself promises,
/// "if somebody applies first, this is refused rather than written over theirs", and it stays refused on a retry
/// because the working copy still names the revision it was taken from.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RefusedApplyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Every_refused_apply_takes_focus_to_its_alert()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "tickets");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForURLAsync("**/schema/tickets");
        await session.PreviewPendingAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);

        await ApplyScenarioSteps.ApplySomebodyElsesChangeAsync(world);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await session.Page.Locator("#apply-reason").FocusAsync();
            await session.Button("Apply these changes").ClickAsync();
            await session.WaitForFocusInsideAsync("error-panel");
        }

        /* Counted by what they say: the staging's own snackbar may still be on screen, and rightly. */
        var headline = await session.Page.GetByTestId("error-title").InnerTextAsync();
        headline.ShouldBe("Somebody applied a revision in between", "the refusal is the revision conflict, not any refusal");
        (await session.SnackbarCountAsync(headline)).ShouldBe(0, "no error is ever a snackbar");
        (await session.Page.GetByText("Applied as revision").CountAsync()).ShouldBe(0);
    }
}

/// <summary>The steps the apply scenarios share.</summary>
internal static class ApplyScenarioSteps
{
    /// <summary>How many revisions Configuration history lists, read once the list has drawn.</summary>
    /// <param name="session">A signed-in session; it is left on the history page.</param>
    /// <returns>The count.</returns>
    public static async Task<int> RevisionCountAsync(AdminSession session)
    {
        await session.GoAsync("/history");
        await session.Page.GetByTestId("revision-row").First.WaitForAsync();
        return await session.Page.GetByTestId("revision-row").CountAsync();
    }

    /// <summary>
    /// Appends a revision straight through the management contract, as the administrator, so the dashboard's working
    /// copy is left naming the revision before it.
    /// </summary>
    /// <param name="world">The running host.</param>
    /// <param name="description">What the change says, different per call when one scenario makes it twice: the same
    /// descriptor again appends no revision.</param>
    public static async Task ApplySomebodyElsesChangeAsync(AdminWorld world, string description = "Changed behind the dashboard.")
    {
        using var scope = world.Services.CreateScope();
        var services = scope.ServiceProvider;
        var ambient = services.GetRequiredService<IAlvoContextAccessor>();
        ambient.Principal = await AdministratorAsync(services);
        try
        {
            var management = services.GetRequiredService<IAlvoManagement>();
            var project = (await management.ListProjectsAsync())[0].Name;
            var current = await management.GetDescriptorAsync(project);
            var changed = JsonNode.Parse(current.DescriptorJson)!.AsObject();
            changed["description"] = description;
            await management.ApplyDescriptorAsync(
                project, new ManagementApplyRequest(changed.ToJsonString(), current.Revision, Reason: "Somebody else"));
        }
        finally
        {
            ambient.Principal = null;
        }
    }

    /// <summary>The bootstrap administrator, as the core authorizes it.</summary>
    internal static async Task<AlvoPrincipal?> AdministratorAsync(IServiceProvider services)
    {
        var people = services.GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey);
        var page = await people.ListAsync(new AlvoUserQuery());
        var administrator = page.Users.Single(
            person => string.Equals(person.Email, AdminWorld.AdminEmail, StringComparison.OrdinalIgnoreCase));
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, administrator.Id.ToString())], "e2e"));
        return await services.GetRequiredService<IAlvoAdminCallerResolver>()
            .ResolveAsync(signedIn, CancellationToken.None);
    }
}
