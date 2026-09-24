using MMLib.Alvo.Admin.Components.Schema.Map;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The descriptor, read into what the system map draws.</summary>
public sealed class SystemGraphTests
{
    private static string Example(string folder, string file)
        => File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", folder, file));

    private static readonly SystemGraph _bikes = SystemGraph.From(Example("bike-workshop", "bike-workshop.alvo.json"));

    [Fact]
    public void Every_entity_is_a_box_and_every_ref_a_wire()
    {
        _bikes.Entities.Count.ShouldBe(8);
        _bikes.Edges.Count(edge => edge.Kind == MapEdgeKind.Reference).ShouldBe(7);
    }

    [Fact]
    public void A_ref_wire_starts_at_its_field_and_says_what_a_delete_does()
    {
        var wire = _bikes.Edges.Single(edge => edge.Kind == MapEdgeKind.Reference && edge.Field == "customer_id" && edge.From == "bikes");

        wire.To.ShouldBe("customers");
        wire.Label.ShouldBe(_bikes.Entities.Single(e => e.Name == "bikes").Fields.Single(f => f.Name == "customer_id")
            .Detail.Split(" · ")[1]);
    }

    [Fact]
    public void A_rollup_is_listed_as_what_it_sums_and_from_where()
    {
        var revenue = _bikes.Entities.Single(e => e.Name == "rental_fleet").Fields.Single(f => f.Name == "revenue");

        revenue.Kind.ShouldBe(MapFieldKind.Rollup);
        revenue.Detail.ShouldBe("Σ sum price from rentals");
        _bikes.Entities.Single(e => e.Name == "customers").Fields.Single(f => f.Name == "bikes_count")
            .Detail.ShouldBe("Σ count from bikes");
    }

    [Fact]
    public void Before_hooks_are_counted_by_what_they_do()
    {
        var orders = _bikes.Entities.Single(e => e.Name == "service_orders");

        orders.Refuses.ShouldBe(2);
        orders.Sets.ShouldBe(1);
        _bikes.Entities.Single(e => e.Name == "order_lines").Refuses.ShouldBe(1);
    }

    [Fact]
    public void After_hooks_wire_the_entity_to_the_template_or_endpoint_they_use()
    {
        var hooks = _bikes.Edges.Where(edge => edge.Kind == MapEdgeKind.Hook).ToList();

        hooks.Select(edge => (edge.From, edge.To, edge.Label)).ShouldBe(
        [
            ("service_orders", "template:express-order-received", "afterCreate"),
            ("service_orders", "template:order-ready", "afterUpdate"),
            ("rentals", "endpoint:rental-desk", "afterCreate"),
        ], ignoreOrder: true);
        hooks.Single(edge => edge.To == "template:order-ready").Condition.ShouldNotBeNull().ShouldContain("new.status == 'ready'");
        _bikes.Outside.Select(node => node.Id).ShouldBe(
            ["template:order-ready", "template:express-order-received", "endpoint:rental-desk"], ignoreOrder: true);
        _bikes.Outside.ShouldAllBe(node => node.Used);
    }

    [Fact]
    public void Automation_rules_are_drawn_as_declared_and_not_yet_running()
    {
        var crm = SystemGraph.From(Example("complex-crm", "crm.alvo.json"));
        var automation = crm.Edges.Where(edge => edge.Kind == MapEdgeKind.Automation).ToList();

        automation.ShouldNotBeEmpty();
        automation.ShouldAllBe(edge => edge.Label == "automation · not yet");
        automation.ShouldContain(edge => edge.From == "deals" && edge.To == "endpoint:invoicing");
    }

    /// <summary>A rule's name travels on its own, so a rule without a condition is still named.</summary>
    [Fact]
    public void An_automation_edge_carries_its_rule_name_apart_from_its_condition()
    {
        var crm = SystemGraph.From(Example("complex-crm", "crm.alvo.json"));
        var invoicing = crm.Edges.Single(edge => edge.Kind == MapEdgeKind.Automation && edge.To == "endpoint:invoicing");

        invoicing.Rule.ShouldBe("deal-won");
        invoicing.Condition.ShouldBe("changed(stage) && new.stage == 'won'");
        crm.Edges.Where(edge => edge.Kind != MapEdgeKind.Automation).ShouldAllBe(edge => edge.Rule == null);
    }

    [Fact]
    public void An_entity_the_applied_descriptor_lacks_or_differs_on_is_pending()
    {
        const string applied = """{"entities":{"a":{"fields":{"name":{"type":"string"}}},"b":{"fields":{"n":{"type":"string"}}}}}""";
        const string working = """{"entities":{"a":{"fields":{"name":{"type":"string"}}},"b":{"fields":{"n":{"type":"text"}}},"c":{"fields":{}}}}""";

        var graph = SystemGraph.From(working, applied);

        graph.Entities.Where(e => e.Pending).Select(e => e.Name).ShouldBe(["b", "c"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"entities":[]}""")]
    [InlineData("""{"entities":{"a":{"fields":{"x":{"type":"ref"}},"hooks":{"afterCreate":"nope"}}},"automation":{"r":{"trigger":{"schedule":"* * * * *"}}}}""")]
    public void A_descriptor_this_screen_did_not_write_draws_what_it_can_and_never_throws(string json)
        => Should.NotThrow(() => SystemGraph.From(json));

    [Fact]
    public void A_self_reference_is_a_wire_from_the_box_to_itself()
    {
        var graph = SystemGraph.From("""{"entities":{"parts":{"fields":{"parent_id":{"type":"ref","entity":"parts","onDelete":"setNull"}}}}}""");

        graph.Edges.ShouldHaveSingleItem().ShouldBe(new MapEdge(MapEdgeKind.Reference, "parts", "parts", "setNull", "parent_id", null, null));
    }

    [Fact]
    public void Focus_keeps_the_centre_one_hop_either_way_and_what_they_wire_to()
    {
        var focused = _bikes.Focus("bikes");

        focused.Entities.Select(e => e.Name).ShouldBe(["customers", "bikes", "service_orders"], ignoreOrder: true);
        focused.Edges.Where(edge => edge.Kind == MapEdgeKind.Reference)
            .ShouldAllBe(edge => focused.Entities.Any(e => e.Name == edge.From) && focused.Entities.Any(e => e.Name == edge.To));
        focused.Outside.Select(node => node.Id).ShouldBe(
            ["template:order-ready", "template:express-order-received"], ignoreOrder: true);
    }

    [Fact]
    public void Outside_nodes_follow_webhooks_before_templates_when_the_descriptor_does()
    {
        var graph = SystemGraph.From("""{"webhooks":{"endpoints":{"w":{}}},"templates":{"t":{}},"entities":{}}""");

        graph.Outside.Select(node => node.Id).ShouldBe(["endpoint:w", "template:t"]);
    }

    [Fact]
    public void Outside_nodes_follow_templates_before_webhooks_when_the_descriptor_does()
    {
        var graph = SystemGraph.From("""{"templates":{"t":{}},"webhooks":{"endpoints":{"w":{}}},"entities":{}}""");

        graph.Outside.Select(node => node.Id).ShouldBe(["template:t", "endpoint:w"]);
    }
}
