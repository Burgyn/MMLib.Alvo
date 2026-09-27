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
    /// field-service's entities publish over realtime by default and it declares a description, so the Overview says
    /// both: the first in the build's sentence, the second in the dashboard's.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_overview_says_what_the_applied_descriptor_declares_and_the_build_does_not_honour()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        var panel = session.Page.GetByTestId("overview-limits");
        await panel.WaitForAsync();
        (await panel.InnerTextAsync()).ShouldContain("Declared, not honoured by this build", Case.Sensitive);

        var realtime = session.Page.GetByTestId("overview-limit-entity.realtime");
        (await realtime.InnerTextAsync()).ShouldContain("no change is published over a realtime channel", Case.Sensitive,
            "the build's own sentence, served as entity.realtime");
        (await session.Page.GetByTestId("overview-unshown-description").InnerTextAsync()).ShouldContain("#268");
        (await session.Page.GetByTestId("overview-limit-auth.providers").CountAsync())
            .ShouldBe(0, "field-service declares local sign-in only, which this build honours");

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
