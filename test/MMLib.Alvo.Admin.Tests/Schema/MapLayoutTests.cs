using MMLib.Alvo.Admin.Components.Schema.Map;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Where the system map puts things — the properties a picture has to keep, not its pixels.</summary>
public sealed class MapLayoutTests
{
    private static readonly SystemGraph _bikes = SystemGraph.From(File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_two_boxes_overlap(bool reactions)
    {
        var picture = MapLayout.Arrange(_bikes, reactions);
        var rects = picture.Boxes.Select(b => (b.X, b.Y, W: MapLayout.BoxWidth, H: b.Height))
            .Concat(picture.Outside.Select(o => (o.X, o.Y, W: MapLayout.OutsideWidth, H: MapLayout.OutsideHeight)))
            .ToList();

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var (a, b) = (rects[i], rects[j]);
                var apart = a.X + a.W <= b.X || b.X + b.W <= a.X || a.Y + a.H <= b.Y || b.Y + b.H <= a.Y;
                apart.ShouldBeTrue($"{i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void A_ref_points_from_a_later_layer_to_an_earlier_one()
    {
        var picture = MapLayout.Arrange(_bikes, reactions: false);
        var layer = picture.Boxes.ToDictionary(b => b.Entity.Name, b => b.Layer);

        foreach (var wire in picture.Wires.Where(w => w.Edge.Kind == MapEdgeKind.Reference && w.Edge.From != w.Edge.To))
        {
            layer[wire.Edge.From].ShouldBeGreaterThan(layer[wire.Edge.To], $"{wire.Edge.From} → {wire.Edge.To}");
        }
    }

    /// <summary>Two reads of one file, not one graph twice — a read that shuffled would pass the latter.</summary>
    [Fact]
    public void The_same_descriptor_draws_the_same_picture()
    {
        var json = Example("bike-workshop", "bike-workshop.alvo.json");

        JsonSerializer.Serialize(MapLayout.Arrange(SystemGraph.From(json), true))
            .ShouldBe(JsonSerializer.Serialize(MapLayout.Arrange(SystemGraph.From(json), true)));
    }

    /// <summary>
    /// crm is five ref layers deep and its automation starts at <c>deals</c>, two columns short of the outside
    /// one: a wire straight across would run under (or over) <c>invoices</c> and <c>invoice_items</c> and read
    /// as theirs. It rides a lane above every box instead, one lane per wire.
    /// </summary>
    [Fact]
    public void An_outside_wire_that_crosses_a_column_rides_a_lane_above_every_box()
    {
        var picture = MapLayout.Arrange(SystemGraph.From(Example("complex-crm", "crm.alvo.json")), reactions: true);
        var last = picture.Boxes.Max(b => b.Layer);
        var outsideWires = picture.Wires.Where(w => w.Edge.Kind != MapEdgeKind.Reference).ToList();
        var crossing = outsideWires.Where(w => picture.Boxes.Single(b => b.Entity.Name == w.Edge.From).Layer < last).ToList();

        crossing.Select(w => w.Edge.From).ShouldBe(["deals", "deals"]);
        crossing.ShouldAllBe(w => w.Lane < picture.Boxes.Min(b => b.Y));
        crossing.Select(w => w.Lane).Distinct().Count().ShouldBe(crossing.Count);
        outsideWires.Except(crossing).ShouldAllBe(w => w.Lane == null);
    }

    [Fact]
    public void A_wire_from_the_last_column_goes_straight_to_its_node()
        => MapLayout.Arrange(SystemGraph.From("""
            {"templates":{"t":{}},"entities":{"a":{"fields":{},"hooks":{"afterCreate":[{"action":{"type":"email","template":"t"}}]}}}}
            """), reactions: true).Wires.ShouldHaveSingleItem().Lane.ShouldBeNull();

    [Fact]
    public void An_unused_outside_node_is_placed_last()
    {
        var graph = SystemGraph.From("""
            {"templates":{"spare":{},"used":{}},
             "entities":{"a":{"fields":{},"hooks":{"afterCreate":[{"action":{"type":"email","template":"used"}}]}}}}
            """);

        var outside = MapLayout.Arrange(graph, reactions: true).Outside;

        outside.Select(o => o.Node.Name).ShouldBe(["used", "spare"]);
        outside[1].Y.ShouldBeGreaterThan(outside[0].Y);
    }

    private static string Example(string folder, string file)
        => File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", folder, file));

    [Fact]
    public void A_cycle_of_references_terminates_and_every_box_is_placed()
    {
        var graph = SystemGraph.From("""
            {"entities":{
              "a":{"fields":{"b_id":{"type":"ref","entity":"b"}}},
              "b":{"fields":{"a_id":{"type":"ref","entity":"a"},"self_id":{"type":"ref","entity":"b"}}}}}
            """);

        var picture = MapLayout.Arrange(graph, reactions: false);

        picture.Boxes.Count.ShouldBe(2);
        picture.Wires.Count.ShouldBe(3);
    }

    [Fact]
    public void Reactions_add_the_outside_column_to_the_right_of_every_box()
    {
        var plain = MapLayout.Arrange(_bikes, reactions: false);
        var reactive = MapLayout.Arrange(_bikes, reactions: true);

        plain.Outside.ShouldBeEmpty();
        plain.Wires.ShouldAllBe(w => w.Edge.Kind == MapEdgeKind.Reference);
        reactive.Outside.Count.ShouldBe(3);
        reactive.Outside.ShouldAllBe(o => o.X >= reactive.Boxes.Max(b => b.X) + MapLayout.BoxWidth);
        reactive.Boxes.Single(b => b.Entity.Name == "service_orders").Guard.ShouldBe("2 refuse · 1 sets");
        plain.Boxes.ShouldAllBe(b => b.Guard == null);
    }

    [Fact]
    public void A_box_shows_its_refs_and_rollups_and_at_most_six_other_fields()
    {
        var box = MapLayout.Arrange(_bikes, false).Boxes.Single(b => b.Entity.Name == "service_orders");
        var entity = box.Entity;

        box.Shown.Count(f => f.Kind == MapFieldKind.Plain).ShouldBeLessThanOrEqualTo(MapLayout.PlainRows);
        box.Shown.Where(f => f.Kind != MapFieldKind.Plain).Count().ShouldBe(entity.Fields.Count(f => f.Kind != MapFieldKind.Plain));
        box.More.ShouldBe(Math.Max(0, entity.Fields.Count(f => f.Kind == MapFieldKind.Plain) - MapLayout.PlainRows));
    }

    [Fact]
    public void An_empty_graph_is_an_empty_picture()
        => MapLayout.Arrange(SystemGraph.Empty, true).ShouldBe(MapPicture.Empty);
}
