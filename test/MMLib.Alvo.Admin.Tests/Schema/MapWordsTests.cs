using MMLib.Alvo.Admin.Components.Schema.Map;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What the map's list for a screen reader says for each wire — the drawing, in words.</summary>
/// <remarks>
/// A condition is not said: it is CEL, which reads aloud as punctuation, and the wire's own tooltip carries it.
/// </remarks>
public sealed class MapWordsTests
{
    [Theory]
    [InlineData(nameof(MapEdgeKind.Reference), "bikes", "customers", "restrict", "customer_id", null,
        "bikes.customer_id points at customers; on delete restrict")]
    [InlineData(nameof(MapEdgeKind.Hook), "service_orders", "template:order-ready", "afterUpdate", null, null,
        "service_orders sends the order-ready template after update")]
    [InlineData(nameof(MapEdgeKind.Hook), "rentals", "endpoint:rental-desk", "afterCreate", null, "new.total > 0",
        "rentals posts to rental-desk after create")]
    [InlineData(nameof(MapEdgeKind.Automation), "deals", "endpoint:invoicing", "automation · not yet", null, "deal-won: changed(stage)",
        "deals posts to invoicing on automation deal-won, which this build does not run yet")]
    [InlineData(nameof(MapEdgeKind.Automation), "deals", "template:welcome", "automation · not yet", null, null,
        "deals sends the welcome template on an automation rule, which this build does not run yet")]
    public void A_wire_is_said_in_one_sentence(
        string kind, string from, string to, string label, string? field, string? condition, string words)
        => MapWords.Of(new MapEdge(Enum.Parse<MapEdgeKind>(kind), from, to, label, field, condition)).ShouldBe(words);
}
