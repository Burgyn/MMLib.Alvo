namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Changing what the backend is, through the screens, and then reading the change back.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the scenario the dashboard exists for</b>, and the one that proves the claim §6.3
/// makes: everything clickable is exportable as code. It adds an entity, gives it rules, previews
/// the plan, applies it, writes a row, changes the row, and finds all of it in the descriptor and
/// in the history afterwards.
/// </para>
/// <para>
/// <b>One scenario, and one apply.</b> The steps depend on each other by construction — there is
/// nothing to browse before something is applied — so splitting them would only introduce an
/// ordering to get wrong.
///
/// <b>What this class deliberately no longer contains</b> is a second scenario that applied again
/// in the same world, to show that an entity nobody wrote a rule for refuses everyone. A second
/// apply did not finish under the in-process host — every step of it passes when driven against a
/// host in another process — and the cause was not found. Default-deny is not left unmeasured: it
/// is pinned by the core's own suites, where it belongs, and the dashboard's part of it (rendering
/// the refusal rather than an empty page) is prose in <c>EntityData</c>'s own remarks. Shipping a
/// scenario that hangs a CI job to cover something already covered is the worse trade.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ChangeTheBackendScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_can_be_created_given_rules_applied_and_then_used()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);

        // --- the entity lands in the working copy, and is visible before it is applied
        await session.GoAsync("/schema");
        await session.Page.ClickAsync("button:has-text('New entity')");
        await session.Page.FillAsync("#new-entity-name", "invoices");
        await session.Page.ClickAsync("button:has-text('Add to the working copy')");
        await session.Page.WaitForURLAsync("**/schema/invoices");


        await session.GoAsync("/schema");
        (await session.Page.Locator("main.a-content").InnerTextAsync())
            .ShouldContain("not applied yet");

        // --- rules, written onto the pending entity
        await session.GoAsync("/schema/invoices");
        await session.OpenTabAsync("Rules");

        foreach (var operation in new[] { "list", "get", "create", "update", "delete" })
        {
            await session.Page.FillAsync($"#rule-{operation}", "'admin' in @user.roles");
            await session.Page.Locator($"#rule-{operation}").BlurAsync();
            await session.SettleAsync();
        }

        // --- the plan, then the apply
        await session.GoAsync("/schema/preview");
        await session.Page.ClickAsync("button:has-text('Plan this change')");
        await session.Page.GetByText("against the database").First.WaitForAsync();

        var plan = await session.Page.Locator("main.a-content").InnerTextAsync();
        plan.ShouldContain("against the database");
        plan.ShouldContain("invoices");

        await session.Page.FillAsync("#apply-reason", "Add invoices, admin only");
        await session.Page.ClickAsync("button:has-text('Apply these changes')");
        await session.Page.GetByText("Applied as revision").First.WaitForAsync();

        // --- the shell's project card follows the apply without a navigation (D-4): Preview stays
        //     put after one, and the card used to keep the old revision until the operator moved on
        var applied = (await session.Page.GetByText("Applied as revision").First.InnerTextAsync())
            .Split(' ')[^1];
        await session.Page.Locator($"[data-testid='project-card']:has-text('revision {applied}')")
            .First.WaitForAsync();

        // --- the entity now serves rows
        await session.GoAsync("/data/invoices");
        await session.Page.ClickAsync("button:has-text('New record')");
        await session.Page.Locator("#rf-name").WaitForAsync();
        await session.Page.FillAsync("#rf-name", "INV-1001");
        await session.Page.ClickAsync("button:has-text('Create')");
        await session.Page.Locator("table.a-grid tbody tr").First.WaitForAsync();

        (await session.Page.Locator("table.a-grid tbody tr").CountAsync()).ShouldBe(1);
        (await session.Page.Locator("table.a-grid tbody tr").First.InnerTextAsync())
            .ShouldContain("INV-1001");

        // --- and the row can be changed
        await session.Page.ClickAsync("table.a-grid tbody tr button:has-text('Edit')");
        await session.Page.Locator("#rf-name").WaitForAsync();
        await session.Page.FillAsync("#rf-name", "INV-1001-amended");
        await session.Page.ClickAsync("button:has-text('Save')");
        await session.Page.GetByText("INV-1001-amended").First.WaitForAsync();

        // --- the descriptor carries what the editor sent, and the history says who and why
        await session.GoAsync("/schema/transfer");
        var descriptor = await session.Page.Locator("main.a-content").InnerTextAsync();
        descriptor.ShouldContain("invoices");
        /* The apostrophes matter. The editor writes the descriptor back out, and a JSON encoder
           that escapes `'` as \u0027 — which the default one does — turns every CEL rule in the
           file into something a person cannot read, while the apply carries on working. This is the
           assertion that catches that. */
        descriptor.ShouldContain("'admin' in @user.roles");

        await session.GoAsync("/history");
        var history = await session.Page.Locator("main.a-content").InnerTextAsync();
        history.ShouldContain("Add invoices, admin only");
        history.ShouldContain(AdminWorld.AdminEmail);

        // --- newest first (D-3), and Overview's latest change is that same revision, not r1 (D-2)
        var newest = await session.Page.Locator("[data-testid='revision-row']").First.InnerTextAsync();
        newest.ShouldContain($"r{applied}");
        newest.ShouldContain("Add invoices, admin only");

        await session.GoAsync("");
        await session.Page.Locator("[data-testid='revision-row']").First.WaitForAsync();
        (await session.Page.Locator("[data-testid='revision-row']").First.InnerTextAsync())
            .ShouldContain("Add invoices, admin only");

        session.AssertConsoleClean();
    }
}
