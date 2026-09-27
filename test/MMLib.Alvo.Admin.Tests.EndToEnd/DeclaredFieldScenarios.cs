using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The Fields tab shows what the descriptor declares of each field: its <c>hidden</c> and <c>readOnly</c> policy
/// (#267's read half) and its description (#268). Read-only: nothing is staged.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class DeclaredFieldScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>field-service declares <c>internal_notes</c> hidden and <c>external_ref</c> read-only.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hidden_and_a_read_only_field_say_so_on_their_rows()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        var hidden = session.Page.GetByTestId("field-row-internal_notes");
        await hidden.WaitForAsync();
        (await hidden.InnerTextAsync()).ShouldContain("hidden");
        (await session.Page.GetByTestId("field-row-external_ref").InnerTextAsync()).ShouldContain("readOnly");
        (await session.Page.GetByTestId("field-row-title").InnerTextAsync()).ShouldNotContain("hidden");

        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_field_row_shows_its_description()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        var description = session.Page.GetByTestId("field-description-title");
        await description.WaitForAsync();
        (await description.InnerTextAsync()).ShouldBe("One line describing the job.");

        session.AssertConsoleClean();
    }
}

/// <summary>
/// A staged field carrying a facet the build refuses says so on its Fields row, before it is opened (#269).
/// </summary>
/// <remarks>
/// The field arrives through Import, which stages without applying: the field editor offers no <c>validation</c> box,
/// because the build refuses it. Its own world, because the import replaces the operator's working copy.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class StagedFieldRefusalScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_staged_field_with_validation_is_badged_refused_on_its_row()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");
        await session.Page.FillAsync("#import-json", WithValidatedField());
        await session.Page.GetByTestId("import-run").ClickAsync();
        await session.Page.WaitForURLAsync("**/changes");

        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("field-refused-slug-field.validation").WaitForAsync();
        (await session.Page.GetByTestId("field-row-name").InnerTextAsync()).ShouldNotContain("refused");

        session.AssertConsoleClean();
    }

    /// <summary>field-service with one more field on <c>regions</c>, which declares a <c>validation</c>.</summary>
    private static string WithValidatedField()
    {
        var descriptor = JsonNode.Parse(Descriptors.FieldService)!.AsObject();
        descriptor["entities"]!["regions"]!["fields"]!["slug"] = new JsonObject
        {
            ["type"] = "string",
            ["validation"] = "size(this) > 2",
        };
        return descriptor.ToJsonString();
    }
}
