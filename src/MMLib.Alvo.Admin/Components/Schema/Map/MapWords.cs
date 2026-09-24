namespace MMLib.Alvo.Admin.Components.Schema.Map;

/// <summary>A wire of the system map said as one sentence, for the list a screen reader reads instead of the drawing.</summary>
/// <remarks>
/// <para>
/// <b>One sentence per wire rather than one label for the picture.</b> The prototype said the whole map in the
/// svg's <c>aria-label</c>, which stops being readable at about eight entities; a list can be walked.
/// </para>
/// <para>
/// <b>A condition is not said.</b> It is CEL, which a screen reader reads as punctuation; the wire's tooltip
/// carries it.
/// </para>
/// </remarks>
internal static class MapWords
{
    /// <summary>The sentence for one edge.</summary>
    /// <param name="edge">The edge the wire draws.</param>
    public static string Of(MapEdge edge) => edge.Kind switch
    {
        MapEdgeKind.Reference => $"{edge.From}.{edge.Field} points at {edge.To}; on delete {edge.Label}",
        MapEdgeKind.Hook => $"{edge.From} {Reaches(edge.To)} {Spoken(edge.Label)}",
        _ => $"{edge.From} {Reaches(edge.To)} on {Rule(edge.Condition)}, which this build does not run yet",
    };

    /// <summary>"sends the order-ready template" / "posts to rental-desk", from an outside node's id.</summary>
    /// <param name="id">A <see cref="MapOutside.Id"/>: <c>template:name</c> or <c>endpoint:name</c>.</param>
    private static string Reaches(string id)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        var name = id[(colon + 1)..];

        return id.StartsWith("template:", StringComparison.Ordinal) ? $"sends the {name} template" : $"posts to {name}";
    }

    /// <summary>"automation deal-won", read off the rule name the graph prefixes to a rule's condition.</summary>
    /// <remarks>
    /// A rule with no condition carries no name in its edge, so it is "an automation rule" — true, and less than
    /// the drawing says only by the name.
    /// </remarks>
    /// <param name="condition">The edge's condition: <c>rule-name: cel</c>, or null.</param>
    private static string Rule(string? condition)
    {
        var colon = condition?.IndexOf(": ", StringComparison.Ordinal) ?? -1;

        return colon > 0 ? $"automation {condition![..colon]}" : "an automation rule";
    }

    /// <summary>A hook point as words: <c>afterUpdate</c> is "after update".</summary>
    /// <param name="point">The hook point, camel-cased as the descriptor spells it.</param>
    private static string Spoken(string point)
        => string.Concat(point.Select(c => char.IsUpper(c) ? $" {char.ToLowerInvariant(c)}" : c.ToString()));
}
