using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Host code on the dashboard (spec 2026-10-14 HX-04 acceptance (3)): the entity list's count and the read-only lane.</summary>
/// <param name="world">The field-service world with two handlers, and a browser.</param>
public sealed class ExtensionCatalogScenarios(ExtensionWorld world) : IClassFixture<ExtensionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_entity_list_counts_the_extensions_that_run_on_each_entity()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        var orders = session.Page.GetByTestId("entity-extensions-work_orders");
        await orders.WaitForAsync();
        (await orders.InnerTextAsync()).Trim().ShouldBe("⟨/⟩ 3");
        (await orders.GetAttributeAsync("aria-label")).ShouldBe("3 extensions from code run on this entity");
        (await session.Page.GetByTestId("entity-extensions-regions").InnerTextAsync()).Trim()
            .ShouldBe("⟨/⟩ 1", "an every-entity handler runs on regions too");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_lifecycle_tab_shows_code_after_the_commit_read_only()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 375);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var lane = session.Page.GetByTestId("lifecycle-after-commit");
        await lane.WaitForAsync();
        var item = lane.GetByTestId($"lane-extension-{ExtensionWorld.Reschedule}");
        var text = await item.InnerTextAsync();
        text.ShouldContain("from code");
        text.ShouldContain(ExtensionWorld.RescheduleDescription);
        (await item.GetByTestId("lane-extension-when").InnerTextAsync()).Trim().ShouldBe("when changed(scheduled_for)");
        (await item.GetByTestId("lane-extension-summary").InnerTextAsync()).Trim()
            .ShouldBe("on update · timeout 30 s · runs as the system · in order per record");
        (await item.GetByRole(AriaRole.Button).CountAsync()).ShouldBe(0, "host code is read-only on the dashboard");
        (await lane.GetByTestId($"lane-extension-{ExtensionWorld.Audit}").InnerTextAsync()).ShouldContain("every entity");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_lifecycle_tab_shows_a_typed_handler_s_condition_as_cel()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var item = session.Page.GetByTestId("lifecycle-after-commit").GetByTestId($"lane-extension-{ExtensionWorld.Completed}");
        await item.WaitForAsync();

        (await item.GetByTestId("lane-extension-when").InnerTextAsync()).Trim()
            .ShouldBe("when changed(status) && new.status == 'completed'");
        session.AssertConsoleClean();
    }
}

/// <summary>The apply preview names the extension a change would unbind (spec 2026-10-14 §4.13, apply preview row).</summary>
/// <remarks>Its own world, because it stages.</remarks>
/// <param name="world">The field-service world with two handlers, and a browser.</param>
public sealed class ExtensionApplyRefusalScenarios(ExtensionWorld world) : IClassFixture<ExtensionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_field_an_extension_reads_is_refused_at_preview_naming_the_extension()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("remove-field-scheduled_for").ClickAsync();
        await session.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();

        await session.Page.ClickAsync("[data-testid='pending-preview']");
        var refusal = session.Page.GetByTestId("error-panel");
        await refusal.WaitForAsync();

        var said = await refusal.InnerTextAsync();
        said.ShouldContain("The descriptor is not valid");
        said.ShouldContain("/entities/work_orders/fields/scheduled_for");
        said.ShouldContain($"'{ExtensionWorld.Reschedule}' (eventHandler, in this host)");
        (await session.Page.GetByTestId("plan").CountAsync()).ShouldBe(0, "a refused descriptor has no plan");
        session.AssertConsoleClean();
    }
}
