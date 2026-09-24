namespace MMLib.Alvo.Admin.Components.Schema.Map;

/// <summary>A wire of the system map said as one sentence, for the list a screen reader reads instead of the drawing.</summary>
/// <remarks>
/// <para>
/// <b>One sentence per wire rather than one label for the picture.</b> The prototype said the whole map in the
/// svg's <c>aria-label</c>, which stops being readable at about eight entities; a list can be walked.
/// </para>
/// <para>
/// <b>A condition is not said.</b> It is CEL, which a screen reader reads as punctuation; the wire's tooltip
/// (<see cref="Tooltip"/>) carries it.
/// </para>
/// </remarks>
internal static class MapWords
{
    /// <summary>The sentence for one edge.</summary>
    /// <param name="edge">The edge the wire draws.</param>
    public static string Of(MapEdge edge) => edge.Kind switch
    {
        MapEdgeKind.Reference => $"{edge.From}.{edge.Field} points at {edge.To}; on delete {Spoken(edge.Label)}",
        MapEdgeKind.Hook => $"{edge.From} {Reaches(edge.To)} {Spoken(edge.Label)}",
        _ => $"{edge.From} {Reaches(edge.To)} on {Rule(edge.Rule)}, which this build does not run yet",
    };

    /// <summary>
    /// A wire's tooltip: its label, then — for an automation — the rule's name, then the condition when there is one.
    /// </summary>
    /// <param name="edge">The edge the wire draws.</param>
    public static string Tooltip(MapEdge edge)
    {
        var rule = edge.Rule is { Length: > 0 } name ? $" — {name}" : string.Empty;
        if (edge.Condition is not { Length: > 0 } condition)
        {
            return edge.Label + rule;
        }

        return rule.Length > 0 ? $"{edge.Label}{rule}: {condition}" : $"{edge.Label} — {condition}";
    }

    /// <summary>"sends the order-ready template" / "posts to rental-desk", from an outside node's id.</summary>
    /// <param name="id">A <see cref="MapOutside.Id"/>: <c>template:name</c> or <c>endpoint:name</c>.</param>
    private static string Reaches(string id)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        var name = id[(colon + 1)..];

        return id.StartsWith("template:", StringComparison.Ordinal) ? $"sends the {name} template" : $"posts to {name}";
    }

    /// <summary>"automation deal-won", or "an automation rule" for an edge that somehow carries no name.</summary>
    /// <param name="rule">The edge's <see cref="MapEdge.Rule"/>.</param>
    private static string Rule(string? rule) => rule is { Length: > 0 } name ? $"automation {name}" : "an automation rule";

    /// <summary>A camel-cased descriptor word as words: <c>afterUpdate</c> is "after update", <c>setNull</c> "set null".</summary>
    /// <param name="word">The word as the descriptor spells it.</param>
    private static string Spoken(string word)
        => string.Concat(word.Select(c => char.IsUpper(c) ? $" {char.ToLowerInvariant(c)}" : c.ToString()));
}
