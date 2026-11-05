using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Write hooks on the dashboard (spec 2026-10-14 §7 HX-09): the two write lanes, the editors' note, the simulator's code list.</summary>
/// <param name="world">The field-service world with write hooks, and a browser.</param>
public sealed class WriteHookScenarios(WriteHookWorld world) : IClassFixture<WriteHookWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1400)]
    [InlineData(375)]
    public async Task The_lifecycle_tab_draws_validate_before_the_transaction_and_the_rest_inside_read_only(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var check = session.Page.GetByTestId("lifecycle-before-transaction").GetByTestId($"lane-extension-{WriteHookWorld.Check}");
        await check.WaitForAsync();
        (await check.GetByTestId("lane-extension-top-level").CountAsync()).ShouldBe(1);
        var inside = session.Page.GetByTestId("lifecycle-in-transaction");
        (await inside.GetByTestId($"lane-extension-{WriteHookWorld.Stamp}").GetByTestId("lane-extension-when").InnerTextAsync()).Trim().ShouldBe("when changed(status)");
        (await inside.GetByTestId($"lane-extension-{WriteHookWorld.Tidy}").GetByTestId("lane-extension-stage").InnerTextAsync()).Trim().ShouldBe("after delete");
        (await session.Page.GetByTestId("lifecycle-before-transaction").GetByRole(AriaRole.Button).CountAsync()).ShouldBe(0, "host code is read-only on the dashboard");
        (await inside.GetByRole(AriaRole.Button).CountAsync()).ShouldBe(0, "host code is read-only on the dashboard");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    /// <summary>A hook that ran shows this instance's p95 and its rejections in a day (Ruling 29).</summary>
    /// <remarks>
    /// The stats call is refused only below the Viewer level, which already keeps the schema — and so this tab — shut, so
    /// no operator who can open the tab is refused its numbers; the refused-stats fallback is a unit's, not a scenario's.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_that_ran_shows_its_p95_and_its_rejections_on_the_lifecycle_tab()
    {
        var tenant = TenantId.New();
        await RecordEditorScenarios.SeedWorkOrderAsync(world, tenant, "WO-0901");
        await RecordConflictScenarios.ElsewhereAsync(world, tenant, "work_orders", "reference", "WO-0901",
            (data, id, system) => data.UpdateAsync("work_orders", id, new Dictionary<string, object?> { ["status"] = "in_progress" }, system));

        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var numbers = session.Page.GetByTestId("lifecycle-in-transaction")
            .GetByTestId($"lane-extension-{WriteHookWorld.Stamp}").GetByTestId("lane-extension-numbers");
        await numbers.WaitForAsync();
        (await numbers.InnerTextAsync()).Trim().ShouldMatch(@"^p95 [\d.]+ ms · 0 rejections in 24 h$");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_on_write_tab_says_code_also_runs_and_links_to_the_lifecycle_tab()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=on-write");

        var note = session.Page.GetByTestId("code-also-runs-update");
        await note.WaitForAsync();
        (await note.InnerTextAsync()).ShouldContain("2 extensions from code also run on this operation");
        (await session.Page.GetByTestId("code-also-runs-delete").InnerTextAsync()).ShouldContain("1 extension from code also runs");

        await note.GetByRole(AriaRole.Link).ClickAsync();
        await session.Page.GetByTestId("lifecycle-in-transaction").WaitForAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_rules_tab_says_code_also_runs_on_an_operation()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=rules");

        var note = session.Page.GetByTestId("code-also-runs-update");
        await note.WaitForAsync();
        (await note.InnerTextAsync()).ShouldContain("2 extensions from code also run on this operation");
        (await note.GetByRole(AriaRole.Link).GetAttributeAsync("href")).ShouldNotBeNull().ShouldEndWith("?tab=lifecycle");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_simulator_lists_the_code_that_also_runs_on_the_simulated_operation()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");

        await PolicySimulation.ChooseOperationAsync(session, "update");

        var listed = session.Page.GetByTestId("verdict-extensions");
        await listed.WaitForAsync();
        (await listed.GetByTestId($"verdict-extension-{WriteHookWorld.Stamp}").CountAsync()).ShouldBe(1);
        (await listed.GetByTestId($"verdict-extension-{WriteHookWorld.Tidy}").CountAsync()).ShouldBe(0, "an after-delete hook does not run on an update");
        session.AssertConsoleClean();
    }
}

/// <summary>A host with handlers and commands but no write hook draws no write lane (Ruling 29).</summary>
/// <param name="world">The extension world.</param>
public sealed class WriteLaneAbsenceScenarios(ExtensionWorld world) : IClassFixture<ExtensionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_host_with_no_write_hook_draws_no_write_lanes()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");
        await session.Page.GetByTestId("lifecycle-after-commit").WaitForAsync();

        (await session.Page.GetByTestId("lifecycle-before-transaction").CountAsync()).ShouldBe(0);
        (await session.Page.GetByTestId("lifecycle-in-transaction").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
}

/// <summary>Picks the simulator's operation the way an operator does: the chip in its radio group, then waits for it to hold.</summary>
internal static class PolicySimulation
{
    internal static async Task ChooseOperationAsync(AdminSession session, string operation)
    {
        var group = session.Page.GetByRole(AriaRole.Radiogroup, new() { Name = "Operation" });
        await group.GetByRole(AriaRole.Radio, new() { Name = operation, Exact = true }).ClickAsync();
        await group.GetByRole(AriaRole.Radio, new() { Name = operation, Exact = true, Checked = true }).WaitForAsync();
    }
}
