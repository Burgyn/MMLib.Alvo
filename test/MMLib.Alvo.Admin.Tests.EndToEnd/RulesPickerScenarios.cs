using Microsoft.Playwright;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The Rules entity picker is a tab strip while the entities fit, and a select past six (spec §4, Rules).</summary>
/// <remarks>
/// Its own class rather than a fact in <see cref="PolicyScenarios"/>, whose remarks record that more than one scenario
/// there did not finish under the in-process host.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RulesPickerScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_tab_in_the_strip_opens_that_entitys_rules()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");
        await session.Page.GetByRole(AriaRole.Tab, new() { Name = "work_orders", Selected = true }).WaitForAsync();

        await session.Page.GetByRole(AriaRole.Tab, new() { Name = "customers", Exact = true }).ClickAsync();

        await session.Page.WaitForURLAsync("**/rules/customers");
        await session.Page.GetByRole(AriaRole.Tab, new() { Name = "customers", Selected = true }).WaitForAsync();
        session.AssertConsoleClean();
    }

    /// <summary>
    /// An entity only the working copy declares has its tab too, with the copy's rules and no simulation, which the
    /// engine serving the applied schema cannot answer (task-6 review, minor 3: item 28's rule on this screen).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_pending_entity_has_a_tab_with_the_working_copys_rules()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Dialog("new-entity").GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("invoices");
        await session.Button("Add to the working copy").ClickAsync();
        await session.Page.WaitForURLAsync("**/schema/invoices");

        await session.GoAsync("/rules/work_orders");
        await session.Page.GetByRole(AriaRole.Tab, new() { Name = "invoices", Exact = true }).ClickAsync();

        await session.Page.WaitForURLAsync("**/rules/invoices");
        await session.Page.GetByTestId("rules-pending").WaitForAsync();
        await session.Page.GetByTestId("simulate-pending").WaitForAsync();
        session.AssertConsoleClean();
    }

    /// <summary>
    /// The page follows the working copy (Task 7 fix round 1, ruling 6): a pending entity another tab removes takes its
    /// tab away, and the page it was on leaves for the first entity's rules.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_pending_entity_removed_in_another_tab_leaves_the_rules_page()
    {
        await using var watcher = await world.SignInAsync(TestContext.Current.CancellationToken);
        await using var remover = await world.SignInAsync(TestContext.Current.CancellationToken);
        await remover.GoAsync("/schema");
        await remover.Button("New entity", exact: true).ClickAsync();
        await remover.Dialog("new-entity").GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("quotes");
        await remover.Button("Add to the working copy").ClickAsync();
        await remover.Page.WaitForURLAsync("**/schema/quotes");

        await watcher.GoAsync("/rules/quotes");
        await watcher.Page.GetByTestId("rules-pending").WaitForAsync();

        await remover.Page.GetByTestId("remove-entity").ClickAsync();
        await remover.Dialog("remove-entity-sheet").GetByRole(AriaRole.Textbox).FillAsync("quotes");
        await remover.Dialog("remove-entity-sheet").GetByTestId("remove-entity-confirm").ClickAsync();
        await remover.Page.WaitForURLAsync("**/admin/schema");

        await watcher.Page.WaitForURLAsync(url => url.EndsWith("/admin/rules", StringComparison.Ordinal));
        await watcher.Page.GetByRole(AriaRole.Tab, new() { Name = "quotes", Exact = true })
            .WaitForAsync(new() { State = WaitForSelectorState.Detached });
        watcher.AssertConsoleClean();
    }
}

/// <summary>Past six entities the Rules picker is a select, and choosing an option opens that entity's rules.</summary>
/// <param name="world">A host booted with seven entities.</param>
public sealed class ManyEntitiesRulesScenarios(ManyEntitiesWorld world) : IClassFixture<ManyEntitiesWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Choosing_an_entity_in_the_select_opens_its_rules()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");
        (await session.Page.GetByRole(AriaRole.Tab).CountAsync()).ShouldBe(0, "seven entities do not fit a strip");

        /* The test id lands on both of the library's combobox elements; the visible one is the one a person uses. */
        await session.ChooseAsync(session.Page.GetByRole(AriaRole.Combobox, new() { Name = "Entity" }), "depots");

        await session.Page.WaitForURLAsync("**/rules/depots");
        await session.Content.GetByText("/api/depots").First.WaitForAsync();
        session.AssertConsoleClean();
    }
}

/// <summary>
/// A world whose descriptor has seven entities: field-service's three, and four global reference lists shaped like its
/// <c>regions</c>.
/// </summary>
public sealed class ManyEntitiesWorld : AdminWorld
{
    private static readonly string[] _extra = ["depots", "skills", "vehicles", "zones"];

    /// <inheritdoc/>
    protected override void Configure(IDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var descriptor = JsonNode.Parse(Descriptors.FieldService)!.AsObject();
        var entities = descriptor["entities"]!.AsObject();
        foreach (var name in _extra)
        {
            entities[name] = entities["regions"]!.DeepClone();
        }

        var path = Path.Combine(Root, "many-entities.json");
        File.WriteAllText(path, descriptor.ToJsonString());
        settings["Alvo:DescriptorPath"] = path;
    }
}
