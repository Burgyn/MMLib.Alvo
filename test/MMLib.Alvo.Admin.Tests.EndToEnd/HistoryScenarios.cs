using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Management;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A revision opens on what it changed (inventory defect #4), and a rollback cannot run without the project's name
/// typed (spec §3.2); a rollback that ran says so in a snackbar, and one that was refused is an alert with focus on it
/// (spec §3.3).
/// </summary>
/// <remarks>
/// Its own world, because it applies: a diff needs a revision before the one it shows. The world boots with its
/// descriptor as r1, the initial one, which has nothing before it. xUnit v3 does not run a class's facts in the order
/// they are written, so each fact applies what it reads rather than leaning on another's applies, and each rollback it
/// runs removes only the entity that fact added. The refused rollback has a world of its own, because it applies behind
/// the dashboard's back and leaves the operator's working copy behind the head.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed partial class HistoryScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_revision_opens_on_its_change_against_the_one_before()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var first = await session.ApplyNewEntityAsync("tickets");
        var second = await session.ApplyNewEntityAsync("invoices");

        await session.GoAsync("/history");
        await Row(session, second).ClickAsync();

        await SelectedTabAsync(session, $"Changes from r{first}");
        var lines = (await session.Page.GetByTestId("revision-diff").InnerTextAsync()).Split('\n');
        var added = lines.Where(line => AddedLine().IsMatch(line)).ToList();
        added.ShouldContain(line => line.Contains("invoices", StringComparison.Ordinal));
        added.ShouldNotContain(
            line => line.Contains("tickets", StringComparison.Ordinal), "the diff is against r(n-1), which has tickets already");

        await session.OpenTabAsync("Descriptor");
        await session.Page.GetByTestId("revision-descriptor").WaitForAsync();

        /* Another revision opens on its change again, whichever tab the last one was left on. */
        await Row(session, first).ClickAsync();
        await session.Page.GetByRole(AriaRole.Tab, new() { Name = "Changes from r", Selected = true }).WaitForAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_first_revision_says_there_is_nothing_before_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/history");

        await session.Page.GetByTestId("revision-row").Last.ClickAsync();

        (await session.Page.GetByTestId("revision-first").InnerTextAsync()).Trim().ShouldBe(
            "This is the descriptor the project started from: there is no earlier revision to compare it with.");
        await session.Page.GetByTestId("revision-descriptor").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_rollback_waits_for_the_project_name_and_Escape_runs_nothing()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.ApplyNewEntityAsync("parts");
        await session.GoAsync("/history");
        var rows = await session.Page.GetByTestId("revision-row").CountAsync();
        await session.Page.GetByTestId("revision-row").Last.ClickAsync();

        await session.Page.GetByTestId("rollback-plan").ClickAsync();
        await session.Page.GetByTestId("rollback-run").ClickAsync();
        var confirm = session.Dialog("rollback-confirm");
        await confirm.WaitForAsync();
        (await confirm.GetByTestId("rollback-confirm-run").IsDisabledAsync()).ShouldBeTrue();

        /* Typed, so the button is live: Escape is what stops it, not a disabled button. */
        await TypeTheProjectNameAsync(session);
        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.GoAsync("/history");
        (await session.Page.GetByTestId("revision-row").CountAsync()).ShouldBe(rows);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_rollback_that_destroys_data_says_so_and_reports_the_revision_it_appended()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.ApplyNewEntityAsync("labels");
        await session.GoAsync("/history");
        var rows = await session.Page.GetByTestId("revision-row").CountAsync();

        await PlanTheRollbackOfTheNewestAsync(session);
        await session.Page.GetByTestId("rollback-destroys").WaitForAsync();
        await session.Page.GetByTestId("rollback-run").ClickAsync();
        var confirm = session.Dialog("rollback-confirm");
        (await confirm.GetByTestId("rollback-confirm-run").InnerTextAsync()).Trim().ShouldBe("Roll back and destroy data");
        await TypeTheProjectNameAsync(session);
        await confirm.GetByTestId("rollback-confirm-run").ClickAsync();

        await session.SnackbarAsync("Rolled back to r");
        await session.Page.GetByTestId("revision-row").Nth(rows).WaitForAsync();
        (await session.Page.GetByTestId("revision-row").CountAsync()).ShouldBe(rows + 1);

        /* The operator's unedited working copy followed the rollback, so their next change is not refused as a conflict. */
        await session.ApplyNewEntityAsync("stickers");
        session.AssertConsoleClean();
    }

    /// <summary>The detail pane beside the list.</summary>
    internal static ILocator Pane(AdminSession session) => session.Page.Locator("#history-revision");

    private static ILocator Row(AdminSession session, string revision)
        => session.Page.GetByTestId("revision-row").Filter(new() { HasTextRegex = new Regex($@"\br{revision}\b") });

    private static Task SelectedTabAsync(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true, Selected = true }).WaitForAsync();

    /// <summary>Opens the revision just before the newest one and asks its rollback plan: a rollback of the newest apply.</summary>
    internal static async Task PlanTheRollbackOfTheNewestAsync(AdminSession session)
    {
        await session.Page.GetByTestId("revision-row").Nth(1).ClickAsync();
        await session.Page.GetByTestId("rollback-plan").ClickAsync();
        await session.Page.GetByTestId("rollback-plan-steps").WaitForAsync();
    }

    internal static async Task TypeTheProjectNameAsync(AdminSession session)
    {
        await session.WaitForFocusOnAsync("confirm-name");
        await session.Page.Keyboard.TypeAsync("field-service");
        await session.Page.WaitForFunctionAsync(
            "() => !document.querySelector(\"[data-testid='rollback-confirm-run']\")?.disabled");
    }

    /// <summary>A line the diff adds: its gutter number, then the sign.</summary>
    [GeneratedRegex(@"^\s*\d*\s*\+ ")]
    private static partial Regex AddedLine();
}

/// <summary>
/// A refused rollback is an alert that takes focus, and never a snackbar (spec §3.3); it is drawn in the detail pane,
/// and the list and the selection stay in place, with a Reload that reads the history again (spec §3.3, §3.6).
/// </summary>
/// <remarks>
/// Its own world, because it applies behind the dashboard's back: the rollback is then asked against a revision that is
/// no longer the head, which the contract refuses rather than writing over the other apply.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RefusedRollbackScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_rollback_is_an_alert_in_the_pane_that_takes_focus_and_Reload_reads_the_history_again()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.ApplyNewEntityAsync("vendors");
        await session.GoAsync("/history");
        await HistoryScenarios.PlanTheRollbackOfTheNewestAsync(session);
        var rows = await session.Page.GetByTestId("revision-row").CountAsync();

        await ApplyScenarioSteps.ApplySomebodyElsesChangeAsync(world);
        await session.Page.GetByTestId("rollback-run").ClickAsync();
        await HistoryScenarios.TypeTheProjectNameAsync(session);
        await session.Dialog("rollback-confirm").GetByTestId("rollback-confirm-run").ClickAsync();

        await session.WaitForFocusInsideAsync("error-panel");
        (await session.SnackbarCountAsync("Rolled back")).ShouldBe(0, "no error is ever a snackbar");
        await HistoryScenarios.Pane(session).GetByTestId("error-panel").WaitForAsync();
        (await session.Page.GetByTestId("revision-row").CountAsync()).ShouldBe(rows, "the list stays in place");
        (await session.Page.GetByTestId("rollback-plan-steps").IsVisibleAsync()).ShouldBeTrue("the selection stays");

        await session.Page.GetByTestId("history-reload").ClickAsync();
        await session.Page.GetByTestId("revision-row").Nth(rows).WaitForAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        await session.Page.GetByTestId("rollback-plan").WaitForAsync(); /* reopened; its stale plan dropped */
        session.AssertConsoleClean();
    }
}

/// <summary>A host whose revision reads can be made to fail once, the way a store that is briefly unreachable does.</summary>
public sealed class FailingRevisionReadWorld : AdminWorld
{
    private int _failures;

    /// <summary>Makes the next revision read throw.</summary>
    public void FailNextRevisionRead() => Interlocked.Exchange(ref _failures, 1);

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
        => ManagementDecorator.Around(services, shipped => new Failing(shipped, this));

    private bool TakeFailure() => Interlocked.Exchange(ref _failures, 0) == 1;

    private sealed class Failing(IAlvoManagement inner, FailingRevisionReadWorld world) : ManagementDecorator(inner)
    {
        public override Task<ManagementRevisionDetail> GetRevisionAsync(
            string project, int revision, CancellationToken ct = default)
            => world.TakeFailure()
                ? throw new InvalidOperationException("The revision store did not answer.")
                : base.GetRevisionAsync(project, revision, ct);
    }
}

/// <summary>A revision that would not open is a refusal in the pane, never the whole screen (spec §3.3, §3.6).</summary>
/// <param name="world">A host whose next revision read can be made to fail.</param>
public sealed class FailedRevisionReadScenarios(FailingRevisionReadWorld world) : IClassFixture<FailingRevisionReadWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_revision_that_would_not_open_keeps_the_list_and_Reload_opens_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/history");
        var rows = await session.Page.GetByTestId("revision-row").CountAsync();

        world.FailNextRevisionRead();
        await session.Page.GetByTestId("revision-row").Last.ClickAsync();

        await session.WaitForFocusInsideAsync("error-panel");
        await HistoryScenarios.Pane(session).GetByTestId("error-panel").WaitForAsync();
        (await session.Page.GetByTestId("revision-row").CountAsync()).ShouldBe(rows, "the list stays in place");

        await session.Page.GetByTestId("history-reload").ClickAsync();
        await session.Page.GetByTestId("revision-first").WaitForAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
    }
}
