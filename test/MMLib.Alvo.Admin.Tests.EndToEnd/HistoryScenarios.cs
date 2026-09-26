using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A revision opens on what it changed (inventory defect #4), and a rollback cannot run without the project's name
/// typed (spec §3.2).
/// </summary>
/// <remarks>
/// Its own world, because it applies: a diff needs a revision before the one it shows. The world boots with its
/// descriptor as r1, the initial one, which has nothing before it. xUnit v3 does not run a class's facts in the order
/// they are written, so each fact applies what it reads rather than leaning on another's applies.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class HistoryScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_revision_opens_on_its_change_against_the_one_before()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var first = await ApplyNewEntityAsync(session, "tickets");
        var second = await ApplyNewEntityAsync(session, "invoices");

        await session.GoAsync("/history");
        await session.Page.GetByTestId("revision-row").Filter(new() { HasText = $"r{second}" }).ClickAsync();

        var selected = session.Page.GetByRole(AriaRole.Tab, new() { Selected = true });
        (await selected.InnerTextAsync()).Trim().ShouldBe($"Changes from r{first}");
        var diff = session.Page.GetByTestId("revision-diff");
        (await diff.InnerTextAsync()).ShouldContain("+ ");
        (await diff.InnerTextAsync()).ShouldContain("invoices");

        await session.OpenTabAsync("Descriptor");
        await session.Page.GetByTestId("revision-descriptor").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_first_revision_says_there_is_nothing_before_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/history");

        await session.Page.GetByTestId("revision-row").Last.ClickAsync();

        await session.Page.GetByTestId("revision-first").WaitForAsync();
        await session.Page.GetByTestId("revision-descriptor").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_rollback_waits_for_the_project_name_and_Escape_runs_nothing()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await ApplyNewEntityAsync(session, "parts");
        await session.GoAsync("/history");
        var rows = await session.Page.GetByTestId("revision-row").CountAsync();
        await session.Page.GetByTestId("revision-row").Last.ClickAsync();

        await session.Page.GetByTestId("rollback-plan").ClickAsync();
        await session.Page.GetByTestId("rollback-run").ClickAsync();
        var confirm = session.Dialog("rollback-confirm");
        await confirm.WaitForAsync();
        (await confirm.GetByTestId("rollback-confirm-run").IsDisabledAsync()).ShouldBeTrue();

        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("revision-row").CountAsync()).ShouldBe(rows);
    }

    private static async Task<string> ApplyNewEntityAsync(AdminSession session, string name)
    {
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", name);
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForURLAsync($"**/schema/{name}");
        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", $"Add {name}");
        await session.Button("Apply these changes").ClickAsync();
        var announced = session.Content.GetByText("Applied as revision").First;
        await announced.WaitForAsync();
        return Regex.Match(await announced.InnerTextAsync(), @"revision (\d+)").Groups[1].Value;
    }
}
