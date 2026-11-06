using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Every refusal the real host publishes reaches a screen, and each "not yet" page finds its own warned block
/// (docs/todo-admin.md §8d items 19 and 20). Read-only: nothing is staged.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class RefusalPlacementScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>The drift guard: a slot the core adds and <c>RefusalPlaces</c> does not place fails here.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task No_refusal_the_build_publishes_is_left_without_a_screen()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.GetByTestId("overview-links").WaitForAsync();
        (await session.Page.GetByTestId("overview-unplaced").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }

    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("/automations")]
    [InlineData("/functions")]
    public async Task A_not_yet_page_finds_its_warned_block_and_shows_the_wildcard_refusal(string route)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync(route);

        await session.Page.GetByTestId("not-yet-warned").WaitForAsync();
        (await session.Page.GetByTestId("not-yet-unknown").CountAsync()).ShouldBe(0);
        await session.Page.GetByTestId("refused-trigger.event").WaitForAsync();

        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Integrations_says_why_a_body_file_and_a_jsonata_payload_are_refused()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");

        await session.Page.GetByTestId("integrations-refused-bodyFile").WaitForAsync();
        await session.Page.GetByTestId("integrations-refused-JSONata").WaitForAsync();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Since HX-11 the rollup filter and the validation are live boxes, so the refused-facets fold names neither — it
    /// keeps only what the build still refuses (a <c>$cel</c> default) — and the rollup section draws the filter's box.
    /// </summary>
    /// <remarks>
    /// Counted rather than waited on: the fold is shut by default, and what is asserted is what it lists, not that it is
    /// open. The sheet is found as the dialog it is (<see cref="AdminSession.Dialog"/>). Choosing a kind stages nothing,
    /// so this class stays read-only.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_field_editor_draws_the_rollup_filter_and_the_validation_as_boxes_not_refusals()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Dialog("field-sheet");
        await sheet.GetByTestId("refused-facets").WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        (await sheet.GetByTestId("refused-field.default").CountAsync()).ShouldBe(1);
        (await sheet.GetByTestId("refused-rollup.where").CountAsync()).ShouldBe(0);
        (await sheet.GetByTestId("refused-field.validation").CountAsync()).ShouldBe(0);
        await sheet.GetByTestId("field-validation").WaitForAsync();

        await sheet.GetByRole(Microsoft.Playwright.AriaRole.Radio, new() { Name = "rollup", Exact = true }).ClickAsync();
        await sheet.GetByTestId("rollup-where").WaitForAsync();

        session.AssertConsoleClean();
    }
}

/// <summary>
/// A pending entity that declares <c>softDelete</c> says in its header that the apply refuses it, instead of a badge that
/// read as a working flag (docs/todo-admin.md §8a softDelete row, §8d item 19).
/// </summary>
/// <remarks>
/// <para>
/// Only a pending entity can carry the flag — the apply refuses it (<c>UnhonouredFeatures.OnAnEntity</c>) — and the
/// New entity editor has no switch for it, so the entity arrives through Import, which stages without applying.
/// </para>
/// <para>Its own world: the import replaces the operator's working copy, which every scenario of a world shares.</para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class PendingSoftDeleteScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_pending_entity_declaring_soft_delete_says_the_apply_refuses_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoToImportAsync();
        await session.Page.FillAsync("#import-json", WithSoftDeletedArchive());
        await session.Page.GetByTestId("import-run").ClickAsync();
        await session.Page.WaitForAddressAsync("**/changes");

        await session.GoAsync("/schema/archives");
        var refusal = session.Page.GetByTestId("refused-entity.softDelete");
        await refusal.WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("Soft delete is not supported yet", Case.Sensitive, "the build's own sentence");

        session.AssertConsoleClean();
    }

    /// <summary>field-service with one more entity, global so it needs no tenant, that declares soft delete.</summary>
    private static string WithSoftDeletedArchive()
    {
        var descriptor = JsonNode.Parse(Descriptors.FieldService)!.AsObject();
        descriptor["entities"]!["archives"] = new JsonObject
        {
            ["tenancy"] = "global",
            ["softDelete"] = true,
            ["fields"] = new JsonObject { ["label"] = new JsonObject { ["type"] = "string" } },
        };
        return descriptor.ToJsonString();
    }
}
