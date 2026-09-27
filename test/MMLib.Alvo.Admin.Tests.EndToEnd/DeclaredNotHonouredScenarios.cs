using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Every key the build ignores is said, in the build's own words, over the real host's capabilities
/// (docs/todo-admin.md §8d items 21 and 27).
/// </summary>
/// <remarks>
/// Its own world: the dynamic-entity case imports into the operator's working copy, which every scenario of a world
/// shares. The Overview case reads the applied descriptor, which the import does not touch.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class DeclaredNotHonouredScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>
    /// field-service declares none of the blocks or keys the build ignores, so the Overview draws no "not honoured"
    /// panel at all; its description and its formats' descriptions, which the build honours and this dashboard does not show, is one quiet line.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_overview_says_nothing_is_ignored_when_nothing_is_and_names_the_unshown_metadata()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        var unshown = session.Page.GetByTestId("overview-unshown");
        await unshown.WaitForAsync();
        (await unshown.InnerTextAsync()).ShouldBe("Declared metadata this dashboard does not show yet: description, formats.*.description (#268, #271).");
        (await session.Page.GetByTestId("overview-limits").CountAsync())
            .ShouldBe(0, "realtime's default is a fact about the build, not a declaration of this project's");

        session.AssertConsoleClean();
    }

    /// <summary>The default-true realtime is said once, where the build is described, in the build's sentence.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Settings_says_once_that_this_build_has_no_realtime()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        var realtime = session.Page.GetByTestId("settings-realtime");
        await realtime.WaitForAsync();
        (await realtime.InnerTextAsync()).ShouldContain("no change is published over a realtime channel", Case.Sensitive,
            "the build's own sentence, served as entity.realtime");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A copy whose only change is a dynamic entity is not told "the schema is unchanged": Preview names it, with the
    /// build's sentence, and says what an apply does with it.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Preview_names_a_dynamic_entity_the_apply_records_and_never_creates()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await StageAsync(session, WithDynamicArchive());

        var plan = session.Page.GetByTestId("plan");
        await plan.WaitForAsync();
        var text = await plan.InnerTextAsync();
        text.ShouldContain("No migration — no table changes", Case.Sensitive);
        text.ShouldNotContain("the schema is unchanged");
        text.ShouldContain(
            "No table changes: archives declare storage: dynamic, which this build does not create; applying records them "
            + "in the descriptor and appends a revision.",
            Case.Sensitive);
        (await session.Page.GetByTestId("plan-dynamic-archives").InnerTextAsync())
            .ShouldContain("no dynamic schema-registry driver", Case.Sensitive, "the build's own sentence, entity.storage");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A <c>storage: dynamic</c> entity is said to be one this build never creates, on the list and on its own screen,
    /// instead of "not applied yet" forever.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_dynamic_entity_is_not_waiting_for_an_apply_that_will_never_create_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await StageAsync(session, WithDynamicArchive());

        await session.GoAsync("/schema");
        var badge = session.Page.GetByTestId("entity-dynamic-archives");
        await badge.WaitForAsync();
        (await badge.InnerTextAsync()).ShouldBe("dynamic — not honoured by this build (F7, #41)");
        (await session.Page.GetByTestId("entity-row-archives").InnerTextAsync()).ShouldNotContain("not applied yet");

        await session.GoAsync("/schema/archives");
        await session.Page.GetByTestId("entity-dynamic").WaitForAsync();
        (await session.Page.GetByTestId("entity-dynamic-note").InnerTextAsync())
            .ShouldContain("no dynamic schema-registry driver", Case.Sensitive, "the build's own sentence, entity.storage");

        session.AssertConsoleClean();
    }

    /// <summary>The long badge wraps rather than pushing the list or the header sideways on a phone.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_dynamic_badge_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await StageAsync(session, WithDynamicArchive());

        await session.GoAsync("/schema");
        await session.Page.GetByTestId("entity-dynamic-archives").WaitForAsync();
        await session.AssertNoHorizontalScrollAsync();

        await session.GoAsync("/schema/archives");
        await session.Page.GetByTestId("entity-dynamic-note").WaitForAsync();
        await session.AssertNoHorizontalScrollAsync();

        session.AssertConsoleClean();
    }

    /// <summary>Stages a descriptor through Import, which lands on Preview without applying.</summary>
    private static async Task StageAsync(AdminSession session, string descriptor)
    {
        await session.GoAsync("/transfer");
        await session.Page.FillAsync("#import-json", descriptor);
        await session.Page.GetByTestId("import-run").ClickAsync();

        var replace = session.Page.GetByTestId("import-replace-run");
        var arrived = session.Page.WaitForURLAsync("**/changes");
        await Task.WhenAny(arrived, replace.WaitForAsync());
        if (!arrived.IsCompleted)
        {
            await replace.ClickAsync();
        }

        await arrived;
    }

    /// <summary>field-service with one more entity, global so it needs no tenant, declared <c>storage: dynamic</c>.</summary>
    private static string WithDynamicArchive()
    {
        var descriptor = JsonNode.Parse(Descriptors.FieldService)!.AsObject();
        descriptor["entities"]!["archives"] = new JsonObject
        {
            ["tenancy"] = "global",
            ["storage"] = "dynamic",
            ["fields"] = new JsonObject { ["label"] = new JsonObject { ["type"] = "string" } },
        };
        return descriptor.ToJsonString();
    }
}
