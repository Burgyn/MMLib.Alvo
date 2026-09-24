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

    [Fact]
    public void The_same_descriptor_draws_the_same_picture()
        => JsonSerializer.Serialize(MapLayout.Arrange(_bikes, true))
            .ShouldBe(JsonSerializer.Serialize(MapLayout.Arrange(_bikes, true)));

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
