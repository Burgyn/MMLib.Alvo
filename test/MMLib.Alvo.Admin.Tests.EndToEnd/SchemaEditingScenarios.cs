using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Changing a field the descriptor already declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Issue #229's own words decide that this belongs here:</b> <i>"change a field or a rule is
/// deliverable without #103; add an entity in the UI is not."</i> What shipped first was the
/// addition — the half that needs a new route — while editing a declared field, the half that
/// needs nothing, had no control at all. A reader of the Fields tab could see every facet and
/// change none of them.
/// </para>
/// <para>
/// <b>Its own world, and one apply in it.</b> <c>ChangeTheBackendScenarios</c> records that a
/// second apply does not finish under the in-process host; this class therefore takes a fresh
/// fixture rather than adding an apply to that one.
/// </para>
/// <para>
/// The facet changed is a widening — a longer <c>maxLength</c> — because it is the change whose
/// plan is unambiguous. Narrowing one, or making a column required, is a change whose cost depends
/// on the rows already in the table, and the screen under test is the editor rather than the
/// migration guard.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class SchemaEditingScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_declared_fields_facet_can_be_changed_previewed_and_applied()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        (await session.Content.InnerTextAsync()).ShouldContain("max 160");

        await session.Page.ClickAsync("[data-testid='edit-field-email']");

        /* Waiting for the heading rather than for the max-length input: that input is in the panel
           in both modes, so waiting on it is waiting for something that is already there — the
           assertions below would then read the panel before the click's re-render arrived. */
        await session.Page.GetByText("Edit email").WaitForAsync();

        /* The name is editable, and this assertion used to say the opposite. It locked the box because
           "replacing a key is a drop and a create" — true while nothing wrote `renamedFrom`, and false
           since the editor does: the apply moves the column and the rows come with it. What is still
           asserted is that the box opens on the name the field has, so an edit that is not a rename
           stays one. `RenameScenarios` covers the rename itself. */
        (await session.Page.Locator("#new-field-name").IsDisabledAsync()).ShouldBeFalse();
        (await session.Page.Locator("#new-field-name").InputValueAsync()).ShouldBe("email");

        await session.Page.FillAsync("#new-field-max", "200");
        await session.Page.ClickAsync("[data-testid='field-save']");
        await session.PreviewPendingAsync();

        /* Not "N steps against the database": on SQLite a longer maxLength is no DDL at all, because
           TEXT carries no length — so the honest signal that the plan came back is the apply control
           appearing, whether or not the plan has steps in it. */
        await session.Page.Locator("#apply-reason").WaitForAsync();
        await session.Page.FillAsync("#apply-reason", "Widen the customer email column");
        await session.Button("Apply these changes").ClickAsync();
        await session.Page.GetByText("Applied as revision").First.WaitForAsync();

        await session.GoAsync("/schema/customers");
        (await session.Content.InnerTextAsync()).ShouldContain("max 200");

        /* The facets the editor cannot draw must survive it. `email` carries a description and a
           `format`, and an editor that rebuilt the declaration from its own controls would drop both
           — silently, with the apply carrying on working, which is exactly the narrowing §4.6
           forbids of the export. */
        await session.GoAsync("/transfer");
        var descriptor = await session.Content.InnerTextAsync();
        descriptor.ShouldContain("Where to send correspondence");
        descriptor.ShouldContain("\"format\": \"email\"");

        session.AssertConsoleClean();
    }
}

/// <summary>
/// Declaring and dropping an index, which used to be the one thing an operator had to leave for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own world for <c>SchemaEditingScenarios</c>' reason</b>: a working copy is composed per
/// operator, so two classes signing in as the same administrator would write one document between
/// them. <c>IClassFixture</c> gives this class a host of its own.
/// </para>
/// <para>
/// <b>Every count below is relative to what the tab already shows, and that is not fastidiousness.</b>
/// The three scenarios here <em>do</em> share one working copy — the fixture is per class, not per
/// scenario, and a copy is composed across screens by design — so a fact asserting "there are now
/// three" passes or fails on the order the runner happens to pick. Which is the order-dependence
/// this suite has already paid for once, and it cost two red scenarios to rediscover.
/// </para>
/// <para>
/// <b>It stops at the preview rather than applying.</b> The change under test is what the editor
/// writes into the descriptor, and the preview is where that document is shown — adding an apply
/// would be measuring the migrator, which has its own suite, at the cost of the second-apply limit
/// <c>ChangeTheBackendScenarios</c> records.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class IndexEditingScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// A composite index is declared from the tab and reaches the descriptor in the schema's shape.
    /// </summary>
    /// <remarks>
    /// The field order is asserted because it is what the index is <em>for</em>: an index on
    /// <c>(a, b)</c> serves a filter on <c>a</c> and does nothing for one on <c>b</c> alone, so a
    /// control that sorted the names would declare a different index from the one that was asked for.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_composite_index_is_declared_from_the_tab()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("Indexes");

        var before = await session.Page.Locator("[data-testid='index-row']").CountAsync();

        var fields = session.Page.GetByTestId("index-fields");
        await fields.GetByRole(AriaRole.Button, new() { Name = "scheduled_for", Exact = true }).ClickAsync();
        await fields.GetByRole(AriaRole.Button, new() { Name = "status", Exact = true }).ClickAsync();
        await session.Page.ClickAsync("[data-testid='index-add']");

        await session.Page.Locator("[data-testid='index-row']").Nth(before).WaitForAsync();

        await session.GoAsync("/changes");
        var previewed = await session.Content.InnerTextAsync();
        previewed.ShouldContain("scheduled_for");
        previewed.ShouldContain("status");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// An index with no fields is refused by the control rather than by the apply.
    /// </summary>
    /// <remarks>
    /// The schema's own <c>minItems: 1</c>, said where it can be acted on. It is the only rule this
    /// control enforces: a second validator in the client is a second set of rules to disagree with,
    /// and the apply is the authority on everything else.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_index_with_no_fields_is_refused_before_the_apply()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("Indexes");

        var before = await session.Page.Locator("[data-testid='index-row']").CountAsync();

        await session.Page.ClickAsync("[data-testid='index-add']");

        /* Waited for rather than read: a click returns when it is dispatched, and the re-render that
           carries the refusal arrives a circuit round trip later. Reading the page straight after the
           click measures the frame before the one under test. */
        await session.Page.Locator("[data-testid='index-refusal']").WaitForAsync();

        (await session.Page.Locator("[data-testid='index-refusal']").InnerTextAsync())
            .ShouldContain("at least one field");
        (await session.Page.Locator("[data-testid='index-row']").CountAsync()).ShouldBe(before);

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Removing takes the row that was asked for, and leaves the other where it was.
    /// </summary>
    /// <remarks>
    /// Removal is by position, because the schema does not forbid two entries over the same fields —
    /// so "the one on (status, priority)" would be ambiguous in exactly the case somebody is cleaning
    /// up. This measures that the position the screen rendered is the position that goes.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_an_index_takes_the_row_it_was_asked_for()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("Indexes");

        var rows = session.Page.Locator("[data-testid='index-row']");
        var before = await rows.CountAsync();
        before.ShouldBeGreaterThan(0, "there is nothing to remove otherwise");

        var went = await rows.First.InnerTextAsync();

        await session.Page.Locator("[data-testid='index-remove']").First.ClickAsync();

        await rows.Nth(before - 1).WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

        /* The row that went is the one that was asked for, not merely one of them. */
        (await rows.AllInnerTextsAsync()).ShouldNotContain(went);

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A single-field index is the field's own facet, and the editor can now write it.
    /// </summary>
    /// <remarks>
    /// The other half of the same gap. `index` was readable — the Fields tab has always badged it —
    /// and unauthorable, so the one index shape an operator reaches for most often was the one the
    /// dashboard could only show them. Asserted against the descriptor rather than the badge, because
    /// the badge comes from the applied schema and this change is not applied — which is what sends
    /// the assertion to the preview's diff rather than to the export screen.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_single_field_index_is_declared_on_the_field_itself()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.ClickAsync("[data-testid='edit-field-title']");
        await session.Page.GetByText("Edit title").WaitForAsync();

        await session.Page.CheckAsync("[data-testid='field-index']");
        await session.Page.ClickAsync("[data-testid='field-save']");

        /* And then for something *on* the preview, which PreviewPendingAsync waits for. The URL moves before
           the screen behind it does, so a read taken on the URL alone reads the entity page it just left —
           which is what this fact did on its first run, and it reported a facet as missing that had been
           written correctly. */
        await session.PreviewPendingAsync();

        /* The preview's diff, not the export screen: export serves the *applied* document, and this
           change has deliberately not been applied. */
        (await session.Page.Locator("main.a-content").InnerTextAsync())
            .ShouldContain("\"index\": true");

        session.AssertConsoleClean();
    }
}

/// <summary>
/// Declaring and dropping a hook, which the tab could only ever show.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own world for <c>IndexEditingScenarios</c>' reason</b>, and its counts are relative for
/// the same one: the scenarios in a class share one working copy.
/// </para>
/// <para>
/// <b>All six points are editable, which is the change.</b> The issue this closes assumed several
/// hook points were still refused; none are — PR5a landed the three <c>after*</c> and PR5b the three
/// <c>before*</c>. What is refused is three of the five action types, and the tab now says so rather
/// than leaving their absence to read as an oversight.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class HookEditingScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// A guarded before-hook is declared, and reaches the descriptor condition first.
    /// </summary>
    /// <remarks>
    /// The before half, because it is the half the schema constrains hardest: a before-hook runs in the
    /// write's own transaction and may only refuse or patch the row.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_guarded_before_hook_is_declared_from_the_tab()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        var before = await session.Page.Locator("[data-testid='hook-row']").CountAsync();

        await session.Page.GetByTestId("hook-points")
            .GetByRole(AriaRole.Radio, new() { Name = "beforeUpdate", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#hook-condition", "old.status == 'completed'");
        await session.Page.FillAsync("#hook-reject", "A completed work order cannot be reopened.");
        await session.Page.ClickAsync("[data-testid='hook-add']");

        await session.Page.Locator("[data-testid='hook-row']").Nth(before).WaitForAsync();

        await session.GoAsync("/changes");
        await session.WaitForPlanAsync();

        var previewed = await session.Content.InnerTextAsync();
        previewed.ShouldContain("beforeUpdate");
        previewed.ShouldContain("cannot be reopened");

        /* And the plan, which is the half worth the wall-clock: the dry run puts the composed
           descriptor through the core's own validator, so a hook written in a shape the apply refuses
           fails here rather than on somebody's deployment. Without this the scenario only proves the
           editor wrote *something* into the document.

           The apply control appearing is the signal the plan came back — this screen renders it only
           then — and the error panel being absent is the signal it came back clean. Both, because a refused
           descriptor leaves the error panel on a screen that still has everything else on it. */
        await session.Page.Locator("#apply-reason").WaitForAsync();

        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Switching to a before-point takes the network actions away with it.
    /// </summary>
    /// <remarks>
    /// The schema's own split, enforced where it can be acted on. Leaving <c>webhook</c> selected after a
    /// move from <c>afterCreate</c> would let the editor compose a descriptor the apply refuses, and the
    /// refusal would name a choice the operator never made.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_before_point_does_not_offer_a_network_action()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('afterCreate')");
        await session.Page.Locator("[data-testid='hook-actions'] button:has-text('webhook')").WaitForAsync();

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('beforeCreate')");

        await session.Page.Locator("[data-testid='hook-actions'] button:has-text('webhook')").WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        await session.Page.Locator("[data-testid='hook-actions'] button:has-text('reject')").WaitForAsync();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// <c>beforeDelete</c> offers no <c>mutate</c>, because the compiler refuses one outright.
    /// </summary>
    /// <remarks>
    /// <b>The one restriction here that the schema does not state.</b> <c>$defs/beforeHookList</c> is one
    /// definition for all three before-points, so reading the split off "is this a before-point" offered
    /// <c>mutate</c> at <c>beforeDelete</c> — which <c>BeforeHookCompiler</c> refuses, because the row is
    /// being removed and a patch against it is a slot that compiles cleanly and is then discarded. Caught by
    /// plan-guard, not by a test, which is why there is now a test.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_before_delete_does_not_offer_a_mutate()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('beforeUpdate')");
        await session.Page.Locator("[data-testid='hook-actions'] button:has-text('mutate')").WaitForAsync();

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('beforeDelete')");

        await session.Page.Locator("[data-testid='hook-actions'] button:has-text('mutate')").WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        await session.Page.Locator("[data-testid='hook-actions'] button:has-text('reject')").WaitForAsync();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The condition guidance names only the row images the selected point actually has.
    /// </summary>
    /// <remarks>
    /// A create has no pre-image and a delete produces no post-image, and <c>BeforeHookCompiler</c> refuses
    /// both references rather than tolerating them — an unanswerable reference resolves to null, and the null
    /// rule collapses every comparison against it, so "every row except…" would fire for every row. Guidance
    /// that named both at every point was guidance to write a descriptor the apply rejects.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_condition_guidance_names_only_the_images_the_point_has()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        var guidance = session.Page.Locator("#hook-condition").Locator("xpath=following-sibling::span[1]");

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('beforeCreate')");
        await session.Page.Locator("[data-testid='hook-actions'] button:has-text('mutate')").WaitForAsync();

        var onCreate = await guidance.InnerTextAsync();
        onCreate.ShouldContain("new");
        onCreate.ShouldNotContain("old");

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('beforeDelete')");

        /* Waited on, not read straight after the click: `InnerTextAsync` does not retry, so reading here
           reads the frame the click has not yet replaced — which is what this scenario did first, and it
           reported the beforeCreate wording under beforeDelete. The mutate chip leaving is the signal that
           the re-render arrived. */
        await session.Page.Locator("[data-testid='hook-actions'] button:has-text('mutate')").WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

        var onDelete = await guidance.InnerTextAsync();
        onDelete.ShouldContain("old");
        onDelete.ShouldNotContain("new");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A reject with no message is refused by the control rather than by the apply.
    /// </summary>
    /// <remarks>
    /// The schema's own <c>required</c>, said where it can be acted on — and the only kind of rule this
    /// editor enforces. Whether the CEL compiles, or the endpoint exists, is the apply's, which answers it
    /// against the whole descriptor rather than against one control.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_reject_with_no_message_is_refused_before_the_apply()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        var before = await session.Page.Locator("[data-testid='hook-row']").CountAsync();

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('beforeDelete')");
        await session.Page.ClickAsync("[data-testid='hook-add']");

        await session.Page.Locator("[data-testid='hook-refusal']").WaitForAsync();
        (await session.Page.Locator("[data-testid='hook-refusal']").InnerTextAsync())
            .ShouldContain("the message the caller reads");
        (await session.Page.Locator("[data-testid='hook-row']").CountAsync()).ShouldBe(before);

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The tab says which action types this build refuses, instead of leaving their absence silent.
    /// </summary>
    /// <remarks>
    /// The audit's finding: <c>FieldEditor</c> filters <c>capabilities.refused</c> to <c>field.*</c> and
    /// nothing consumed the rest, so a missing control for <c>entity.update</c> read as an oversight. The
    /// wording is the framework's, because a reworded refusal is the one nobody tested.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_tab_says_which_action_types_this_build_refuses()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        var refused = await session.Page.Locator("[data-testid='hooks-refused']").InnerTextAsync();
        refused.ShouldContain("entity.update");
        refused.ShouldContain("http.call");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A declared hook does not make the page scroll sideways on a phone.
    /// </summary>
    /// <remarks>
    /// <b>The gap that let a real defect ship:</b> <c>Every_screen_fits_a_phone</c> already walks
    /// <c>/schema/work_orders</c> at 375 px and passed — because the example descriptor declares no hooks,
    /// so the tab it was measuring was the empty state. With one hook on it, the code block sat in a flex row
    /// whose item kept its <c>min-width: auto</c>, refused to shrink under the JSON's longest line, and took
    /// the whole document sideways. Measured with a hook present, and with the refusals rendered, because
    /// those are the two things that make the row wide.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_declared_hook_does_not_push_the_page_sideways_on_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('beforeUpdate')");
        await session.Page.FillAsync("#hook-condition", "old.status == 'completed' && new.status != 'completed'");
        await session.Page.FillAsync("#hook-reject", "A completed work order cannot be reopened.");
        await session.Page.ClickAsync("[data-testid='hook-add']");

        await session.Page.Locator("[data-testid='hook-row']").First.WaitForAsync();

        await session.AssertNoHorizontalScrollAsync();
        await session.AssertNoVerticalTextAsync();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The condition reaches the screen as the author wrote it, not as JSON escaped it.
    /// </summary>
    /// <remarks>
    /// <c>JsonNode.ToJsonString()</c> with no options takes the default encoder, which escapes anything that
    /// could be dangerous in HTML — so <c>&amp;&amp;</c> and an apostrophe reached the tab as
    /// <c>\u0026\u0026</c> and <c>\u0027</c>. Unreadable rather than wrong, and invisible to every fact
    /// that asserted against the document rather than against the render.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_condition_is_rendered_as_it_was_written()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('beforeUpdate')");
        await session.Page.FillAsync("#hook-condition", "old.status == 'completed' && new.status != 'completed'");
        await session.Page.FillAsync("#hook-reject", "A completed work order cannot be reopened.");
        await session.Page.ClickAsync("[data-testid='hook-add']");

        var row = session.Page.Locator("[data-testid='hook-row']").First;
        await row.WaitForAsync();

        var rendered = await row.InnerTextAsync();
        rendered.ShouldContain("&&");
        rendered.ShouldContain("'completed'");
        rendered.ShouldNotContain("\\u0026");
        rendered.ShouldNotContain("\\u0027");

        session.AssertConsoleClean();
    }

    /// <summary>Removing takes the hook that was asked for.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_hook_takes_the_one_it_was_asked_for()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.OpenTabAsync("On write");

        var rows = session.Page.Locator("[data-testid='hook-row']");
        var before = await rows.CountAsync();

        await session.Page.ClickAsync("[data-testid='hook-points'] button:has-text('afterDelete')");
        await session.Page.FillAsync("#hook-endpoint", "dispatch");
        await session.Page.ClickAsync("[data-testid='hook-add']");
        await rows.Nth(before).WaitForAsync();

        await session.Page.Locator("[data-testid='hook-remove']").Last.ClickAsync();
        await rows.Nth(before).WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

        session.AssertConsoleClean();
    }
}

/// <summary>
/// Renaming an entity and a field, which the dashboard could only ever explain.
/// </summary>
/// <remarks>
/// Its own world for <c>IndexEditingScenarios</c>' reason, and it stops at the preview for the same one:
/// what is under test is the document the editor composes, and the migrator has its own suite.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RenameScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// An entity rename carries <c>renamedFrom</c> and takes the operator with it.
    /// </summary>
    /// <remarks>
    /// Both halves. Without the key the apply reads a drop and a create, and the rows go with the old
    /// table; without the navigation the screen is routed by a name the working copy no longer declares
    /// and answers "there is no entity called …" to the person who just renamed it.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_rename_is_carried_rather_than_dropped()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.ClickAsync("[data-testid='rename-entity']");
        await session.Page.FillAsync("#rename-entity-name", "service_areas");
        await session.Page.ClickAsync("[data-testid='rename-save']");

        await session.Page.WaitForURLAsync("**/schema/service_areas");

        /* It plans cleanly: work_orders.region_id followed the rename, so the validator has no ref to an
           entity that is gone. */
        await session.GoAsync("/changes");
        await session.WaitForPlanAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);

        var previewed = await session.Content.InnerTextAsync();
        previewed.ShouldContain("service_areas");
        previewed.ShouldContain("\"renamedFrom\": \"regions\"");
        previewed.ShouldContain("\"entity\": \"service_areas\"");
    }

    /// <summary>
    /// A field rename carries its own <c>renamedFrom</c>, and the facets survive it.
    /// </summary>
    /// <remarks>
    /// The facets are written under the old name and the rename runs after, so the origin is computed from
    /// a declaration that is current. Written the other way round, the editor's stale <c>renamedFrom</c>
    /// lands on the renamed field and the apply is pointed at the wrong column.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_field_rename_is_carried_and_keeps_its_facets()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.ClickAsync("[data-testid='edit-field-email']");
        await session.Page.GetByText("Edit email").WaitForAsync();

        await session.Page.FillAsync("#new-field-name", "contact_email");
        await session.Page.ClickAsync("[data-testid='field-save']");
        await session.PreviewPendingAsync();

        var previewed = await session.Page.Locator("main.a-content").InnerTextAsync();
        previewed.ShouldContain("contact_email");
        previewed.ShouldContain("\"renamedFrom\": \"email\"");

        /* The facets the editor cannot draw must survive a rename exactly as they survive an edit. */
        previewed.ShouldContain("\"format\": \"email\"");
    }

    /// <summary>A name another field already has is refused in the editor, not after it closes.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_field_rename_onto_a_taken_name_is_refused()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.ClickAsync("[data-testid='edit-field-phone']");
        await session.Page.GetByText("Edit phone").WaitForAsync();

        await session.Page.FillAsync("#new-field-name", "name");
        await session.Page.ClickAsync("[data-testid='field-save']");

        await session.Page.GetByText("already declares a field called name").WaitForAsync();

        /* Still open, still on the editor: a refusal the operator can act on without retyping. */
        (await session.Page.Locator("#new-field-name").InputValueAsync()).ShouldBe("name");

        session.AssertConsoleClean();
    }

    /// <summary>An entity name the schema cannot carry is refused, with the pattern.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_name_the_schema_cannot_carry_is_refused()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.ClickAsync("[data-testid='rename-entity']");
        await session.Page.FillAsync("#rename-entity-name", "Service Areas");
        await session.Page.ClickAsync("[data-testid='rename-save']");

        await session.Page.Locator("[data-testid='rename-sheet'] .a-error").WaitForAsync();
        session.Page.Url.ShouldContain("/schema/regions");

        session.AssertConsoleClean();
    }
}

/// <summary>
/// Throwing away a draft, which the dashboard had no control for.
/// </summary>
/// <remarks>
/// <b>Reported from the dashboard, and the two halves are one defect.</b> Removing a field stages the
/// change immediately — deliberately, because nothing has touched the database and a confirmation in
/// front of an edit to a draft trains an operator to click through the one that matters. That argument
/// only holds while there is a way back, and there was none: <c>WorkingCopy.Discard</c> existed and no
/// screen called it, so a staged change could only be applied or abandoned with the session.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class DiscardScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>A staged change can be thrown away, and the editor shows it gone.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_staged_change_can_be_discarded()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.ClickAsync("[data-testid='remove-field-notes']");
        await session.PreviewPendingAsync();
        await session.Page.Locator("[data-testid='discard']").First.WaitForAsync();

        /* The premise: it really is staged before anything was planned. */
        (await session.Content.InnerTextAsync()).ShouldContain("notes");

        await session.Page.Locator("[data-testid='discard']").First.ClickAsync();
        await session.Page.ClickAsync("[data-testid='discard-confirm']");

        await session.Page.WaitForURLAsync("**/schema");

        await session.GoAsync("/changes");
        await session.Page.GetByText("Nothing to apply").WaitForAsync();

        /* And the field is back on the entity, because the copy went to the applied revision. */
        await session.GoAsync("/schema/customers");
        (await session.Content.InnerTextAsync()).ShouldContain("notes");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Discarding is confirmed; the sheet can be dismissed without losing the draft.
    /// </summary>
    /// <remarks>
    /// The one control on this screen that destroys work the operator did, and the only one that cannot be
    /// undone from here — which is why it is the one that asks, while staging does not.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Dismissing_the_confirmation_keeps_the_draft()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.ClickAsync("[data-testid='remove-field-name']");
        await session.PreviewPendingAsync();

        await session.Page.Locator("[data-testid='discard']").First.ClickAsync();
        await session.Page.Locator("[data-testid='discard-sheet']").WaitForAsync();
        await session.Page.ClickAsync("[data-testid='sheet-close']");

        await session.Page.Locator("[data-testid='discard-sheet']").WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

        /* Still staged: dismissing a confirmation is not confirming it. */
        await session.Page.Locator("[data-testid='discard']").First.WaitForAsync();
        (await session.Page.GetByText("Nothing to apply").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }
}
