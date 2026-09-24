using MMLib.Alvo.Admin.Components.Schema.Map;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What the map's list for a screen reader says for each wire — the drawing, in words.</summary>
/// <remarks>
/// A condition is not said: it is CEL, which reads aloud as punctuation, and the wire's own tooltip carries it.
/// </remarks>
public sealed class MapWordsTests
{
    [Theory]
    [InlineData(nameof(MapEdgeKind.Reference), "bikes", "customers", "restrict", "customer_id", null, null,
        "bikes.customer_id points at customers; on delete restrict")]
    [InlineData(nameof(MapEdgeKind.Reference), "parts", "parts", "setNull", "parent_id", null, null,
        "parts.parent_id points at parts; on delete set null")]
    [InlineData(nameof(MapEdgeKind.Hook), "service_orders", "template:order-ready", "afterUpdate", null, null, null,
        "service_orders sends the order-ready template after update")]
    [InlineData(nameof(MapEdgeKind.Hook), "rentals", "endpoint:rental-desk", "afterCreate", null, "new.total > 0", null,
        "rentals posts to rental-desk after create")]
    [InlineData(nameof(MapEdgeKind.Automation), "deals", "endpoint:invoicing", "automation · not yet", null, "changed(stage)", "deal-won",
        "deals posts to invoicing on automation deal-won, which this build does not run yet")]
    [InlineData(nameof(MapEdgeKind.Automation), "deals", "template:welcome", "automation · not yet", null, null, "welcome",
        "deals sends the welcome template on automation welcome, which this build does not run yet")]
    public void A_wire_is_said_in_one_sentence(
        string kind, string from, string to, string label, string? field, string? condition, string? rule, string words)
        => MapWords.Of(new MapEdge(Enum.Parse<MapEdgeKind>(kind), from, to, label, field, condition, rule)).ShouldBe(words);

    [Theory]
    [InlineData(nameof(MapEdgeKind.Reference), "restrict", null, null, "restrict")]
    [InlineData(nameof(MapEdgeKind.Hook), "afterUpdate", "new.status == 'ready'", null, "afterUpdate — new.status == 'ready'")]
    [InlineData(nameof(MapEdgeKind.Automation), "automation · not yet", "changed(stage)", "deal-won", "automation · not yet — deal-won: changed(stage)")]
    [InlineData(nameof(MapEdgeKind.Automation), "automation · not yet", null, "welcome", "automation · not yet — welcome")]
    public void A_wire_s_tooltip_names_its_rule_and_its_condition(string kind, string label, string? condition, string? rule, string tooltip)
        => MapWords.Tooltip(new MapEdge(Enum.Parse<MapEdgeKind>(kind), "a", "b", label, null, condition, rule)).ShouldBe(tooltip);

    /// <summary>
    /// The word drawn on the canvas: an automation's is <c>not yet</c> — its dash and the legend already say
    /// automation, and the full label did not fit the gap before its node.
    /// </summary>
    [Theory]
    [InlineData(nameof(MapEdgeKind.Automation), "automation · not yet", "not yet")]
    [InlineData(nameof(MapEdgeKind.Hook), "afterUpdate", "afterUpdate")]
    public void A_wire_s_word_on_the_canvas_is_short(string kind, string label, string word)
        => MapWords.OnWire(new MapEdge(Enum.Parse<MapEdgeKind>(kind), "a", "b", label, null, null, "r")).ShouldBe(word);

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1 automation rule draws no wire: it runs on a schedule, matches no single entity, or calls something other than a template or webhook.")]
    [InlineData(2, "2 automation rules draw no wire: they run on a schedule, match no single entity, or call something other than a template or webhook.")]
    public void Rules_the_map_cannot_draw_are_owned_up_to(int undrawn, string? sentence)
        => MapWords.Undrawn(undrawn).ShouldBe(sentence);
}
