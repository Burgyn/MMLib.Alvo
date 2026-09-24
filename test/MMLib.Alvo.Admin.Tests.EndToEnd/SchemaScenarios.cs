namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Reading what the backend is.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class SchemaScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_schema_lists_the_entities_the_descriptor_declares()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        var text = await session.Page.Locator("main.a-content").InnerTextAsync();
        text.ShouldContain("regions");
        text.ShouldContain("customers");
        text.ShouldContain("work_orders");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// An entity's six tabs all render, and none of them throws.
    /// </summary>
    /// <remarks>
    /// The cheapest test that would have caught the failure this suite exists for: a tab whose
    /// content throws leaves the chrome, the tab strip and an empty panel — which looks like an
    /// entity with nothing in it rather than like a defect.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Every_tab_of_an_entity_renders_something()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        foreach (var tab in new[] { "Fields", "Relationships", "Rules", "On write", "Indexes", "API" })
        {
            await session.OpenTabAsync(tab);

            var panel = await session.Page.Locator("main.a-content .a-panel").Last.InnerTextAsync();
            panel.Trim().ShouldNotBeEmpty($"the {tab} tab rendered nothing");
        }

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The facets shown are the entity's own, read from the schema rather than assumed.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_fields_facets_are_the_ones_the_descriptor_gave_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        var fields = await session.Page.Locator("main.a-content").InnerTextAsync();
        fields.ShouldContain("reference");
        fields.ShouldContain("unique");
        fields.ShouldContain("work-order-ref");
        fields.ShouldContain("max 24");
    }

    /// <summary>
    /// The field editor draws a default-value control, and still says which half of the feature is
    /// refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal <c>field.default</c> is honoured — it becomes a column <c>DEFAULT</c> — so the editor
    /// offers a box for it. The <c>$cel</c> half is not, because a CEL default is evaluated against the
    /// caller's context at insert time, which is the <c>computed</c> machinery rather than a column
    /// default; an editor silent about that line is indistinguishable from one that forgot it.
    /// </para>
    /// <para>
    /// The sentence is the framework's own, served verbatim from <c>capabilities</c> (§2.3), so this
    /// asserts a fragment of the stored prose rather than a wording this screen invented.
    /// </para>
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_field_editor_offers_a_default_and_names_the_half_it_refuses()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.ClickAsync("[data-testid='add-field']");
        await session.Page.Locator("#new-field-default").WaitForAsync();

        /* The refusals are folded into one line at the end of the sheet, so the operator adding a field
           is not reading them first; the one who asks where the default went opens it. */
        await session.Page.ClickAsync("[data-testid='refused-facets'] > summary");
        var refusal = session.Page.Locator("[data-testid='refused-field.default']");
        await refusal.WaitForAsync();

        var text = await refusal.InnerTextAsync();
        text.ShouldContain("not as a '$cel' expression");
        text.ShouldContain("Declare a literal default");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The columns Alvo maintains are shown as Alvo's, not as the author's.
    /// </summary>
    /// <remarks>
    /// Mixing them into the declared list is how somebody ends up trying to write
    /// <c>created_by</c> — and an audit trail a caller can author is not an audit trail.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_managed_columns_are_separated_from_the_declared_ones()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        var text = await session.Page.Locator("main.a-content").InnerTextAsync();
        text.ShouldContain("Maintained by Alvo");
        text.ShouldContain("framework writes it");
    }

    /// <summary>
    /// The API tab says what it cannot do, rather than listing five routes that answer 404.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_api_tab_says_a_runtime_entity_waits_for_a_restart()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("API");

        var text = await session.Page.Locator("main.a-content").InnerTextAsync();
        text.ShouldContain("after a restart");
        text.ShouldContain("/api/work_orders");
    }

    /// <summary>
    /// The API tab links the generated contract, and the link resolves.
    /// </summary>
    /// <remarks>
    /// <b>The route is asserted by fetching it, not by comparing two constants.</b> The dashboard cannot see
    /// the host's route — it references Abstractions alone — so the path arrives through
    /// <c>AlvoAdminOptions</c>, and the failure this guards against is exactly that the two halves drift
    /// into a link the screen offers and the host answers 404 for. Which is what the tab did before: it
    /// named <c>/openapi/v1.json</c> in prose, on every deployment, whether or not one was served.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_api_tab_links_documentation_the_host_actually_serves()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("API");

        foreach (var control in new[] { "api-docs", "api-document" })
        {
            var href = await session.Page.Locator($"[data-testid='{control}']").GetAttributeAsync("href");
            href.ShouldNotBeNullOrEmpty();

            var served = await session.Page.APIRequest.GetAsync($"{world.BaseAddress}{href}");
            served.Status.ShouldBe(200, $"{control} points at {href}, which this host does not serve.");
        }

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Settings links it too, because that is the other place an operator looks for it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Settings_links_the_documentation_as_well()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        (await session.Page.Locator("[data-testid='settings-docs']").GetAttributeAsync("href"))
            .ShouldNotBeNullOrEmpty();
        (await session.Page.Locator("[data-testid='settings-document']").GetAttributeAsync("href"))
            .ShouldNotBeNullOrEmpty();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Exporting is the stored document, not a re-serialisation of it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_export_screen_shows_the_descriptor_as_stored()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");

        var text = await session.Page.Locator("main.a-content").InnerTextAsync();
        text.ShouldContain("byte for byte");
        text.ShouldContain("field-service");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// An imported descriptor whose names the frozen schema refuses is refused before it renders.
    /// </summary>
    /// <remarks>
    /// Two things at once, and both matter: the apply would reject it anyway, so a control that
    /// accepted it would be producing a descriptor the apply refuses; and the Import box is the one
    /// place this application takes input it did not produce, while every screen builds markup from
    /// names.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_imported_descriptor_with_a_name_the_schema_refuses_is_refused()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");

        await session.Page.FillAsync(
            "#import-json",
            """{"name":"borrowed","entities":{"Things":{"fields":{"a":{"type":"string"}}}}}""");
        await session.Page.ClickAsync("button:has-text('Load it into the working copy')");
        await session.SettleAsync();

        session.Page.Url.ShouldContain("/transfer");
        (await session.Page.Locator(".a-error__title").InnerTextAsync())
            .ShouldContain("apply would refuse");
        session.AssertConsoleClean();
    }
}
