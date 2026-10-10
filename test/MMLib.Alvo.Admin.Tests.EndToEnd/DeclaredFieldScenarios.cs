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
        await StageAsync(session, WithValidatedField());

        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("field-refused-slug-field.validation").WaitForAsync();
        (await session.Page.GetByTestId("field-row-name").InnerTextAsync()).ShouldNotContain("refused");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// The row's badge agrees with the build, form by form: the three forms the badge detects are the ones the real
    /// host's dry run refuses, and the honoured neighbour of each is accepted and not badged.
    /// </summary>
    /// <remarks>
    /// <b>This is what pins <c>FieldBadges.Refused</c>'s detection to the core's</b>: the Admin cannot call
    /// <c>ValueOrExpr.IsTaggedExpression</c> or <c>RollupResolver</c>, which are internal to the core, so it restates
    /// them — and this asks the build itself, through Preview's dry run of the same staged field, whether each form is
    /// refused. A core that starts accepting a form the row still badges, or refusing one it does not, fails here.
    /// </remarks>
    /// <param name="declaration">The staged field's declaration.</param>
    /// <param name="slot">The slot the row badges, or empty when the build accepts it.</param>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("""{"type":"string","validation":"size(this) > 2"}""", "field.validation")]
    [InlineData("""{"type":"string","default":{"$cel":"'x'"}}""", "field.default")]
    [InlineData("""{"type":"string","default":"x"}""", "")]
    [InlineData("""{"type":"integer","rollup":{"from":"work_orders","op":"count","where":"priority > 1"}}""", "rollup.where")]
    [InlineData("""{"type":"integer","rollup":{"from":"work_orders","op":"count"}}""", "")]
    public async Task The_row_badges_exactly_what_the_build_refuses(string declaration, string slot)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await StageAsync(session, With("customers", "probe", JsonNode.Parse(declaration)!.AsObject()));

        var refusedByBuild = session.Page.GetByTestId("error-panel");
        await refusedByBuild.Or(session.Page.GetByTestId("plan")).First.WaitForAsync();
        var refused = await refusedByBuild.CountAsync() > 0;
        refused.ShouldBe(slot.Length > 0, "the build's dry run is the authority on which form is refused");

        await session.GoAsync("/schema/customers");
        var row = session.Page.GetByTestId("field-row-probe");
        await row.WaitForAsync();
        if (refused)
        {
            await session.Page.GetByTestId($"field-refused-probe-{slot}").WaitForAsync();
        }
        else
        {
            (await row.InnerTextAsync()).ShouldNotContain("refused at apply");
        }

        session.AssertConsoleClean();
    }

    /// <summary>
    /// Stages a descriptor through Import, which lands on Preview without applying — confirming the replacement when an
    /// earlier case of this class left the copy staged.
    /// </summary>
    private static async Task StageAsync(AdminSession session, string descriptor)
    {
        await session.GoToImportAsync();
        await session.Page.FillAsync("#import-json", descriptor);
        await session.Page.GetByTestId("import-run").ClickAsync();

        var replace = session.Page.GetByTestId("import-replace-run");
        var arrived = session.Page.WaitForAddressAsync("**/changes");
        await Task.WhenAny(arrived, replace.WaitForAsync());
        if (!arrived.IsCompleted)
        {
            await replace.ClickAsync();
        }

        await arrived;
    }

    /// <summary>field-service with one more field on <c>regions</c>, which declares a <c>validation</c>.</summary>
    private static string WithValidatedField()
        => With("regions", "slug", new JsonObject { ["type"] = "string", ["validation"] = "size(this) > 2" });

    /// <summary>field-service with one more field on one entity.</summary>
    private static string With(string entity, string field, JsonObject declaration)
    {
        var descriptor = JsonNode.Parse(Descriptors.FieldService)!.AsObject();
        descriptor["entities"]![entity]!["fields"]![field] = declaration;
        return descriptor.ToJsonString();
    }
}
