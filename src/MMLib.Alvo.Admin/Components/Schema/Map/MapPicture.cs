namespace MMLib.Alvo.Admin.Components.Schema.Map;

/// <summary>One entity box, placed in the layout.</summary>
/// <param name="Entity">The entity the box draws.</param>
/// <param name="Layer">0 = referenced by the others and pointing at nothing drawn; higher = further right.</param>
/// <param name="X">The box's left edge.</param>
/// <param name="Y">The box's top edge.</param>
/// <param name="Height">The box's full height, header through the trailing "+n more" row.</param>
/// <param name="Shown">The rows drawn: every Reference and Rollup field, then up to 6 Plain ones, in descriptor order within each group.</param>
/// <param name="More">How many Plain fields were left out.</param>
/// <param name="Guard">"2 refuse · 1 sets" (only the non-zero parts, "refuse"/"sets" never pluralised) when reactions are on and the entity has before-hooks; else null.</param>
internal sealed record PlacedBox(
    MapEntity Entity, int Layer, double X, double Y, double Height, IReadOnlyList<MapField> Shown, int More, string? Guard)
{
    /// <summary>The y of a shown row's baseline centre — where a ref wire leaves the box.</summary>
    /// <param name="index">The row's index into <see cref="Shown"/>.</param>
    public double RowY(int index)
        => Y + MapLayout.Head + (Guard is null ? 0 : MapLayout.Row) + index * MapLayout.Row + 8;
}

/// <summary>One template or webhook-endpoint node, placed in the outside column.</summary>
/// <param name="Node">The outside node the marker draws.</param>
/// <param name="X">The node's left edge.</param>
/// <param name="Y">The node's top edge.</param>
internal sealed record PlacedOutside(MapOutside Node, double X, double Y);

/// <summary>One wire, drawn as a path between two placed elements.</summary>
/// <param name="Edge">The edge this wire draws.</param>
/// <param name="Path">An SVG path `d`.</param>
/// <param name="StartX">Where the wire leaves its source, x.</param>
/// <param name="StartY">Where the wire leaves its source, y.</param>
/// <param name="EndX">Where the wire reaches its target, x — a hook's word is drawn just before it.</param>
/// <param name="EndY">Where the wire reaches its target, y.</param>
internal sealed record PlacedWire(MapEdge Edge, string Path, double StartX, double StartY, double EndX, double EndY);

/// <summary>The whole placed picture <see cref="MapLayout.Arrange"/> produces.</summary>
/// <param name="Boxes">Every entity box, placed.</param>
/// <param name="Outside">Every outside node, placed — empty unless reactions are drawn.</param>
/// <param name="Wires">Every wire, placed.</param>
/// <param name="Width">The picture's full width, the right edge of its rightmost element.</param>
/// <param name="Height">The picture's full height, the bottom edge of its lowest element.</param>
internal sealed record MapPicture(
    IReadOnlyList<PlacedBox> Boxes, IReadOnlyList<PlacedOutside> Outside, IReadOnlyList<PlacedWire> Wires, double Width, double Height)
{
    /// <summary>The picture of nothing — an empty graph draws no boxes, no wires, no extent.</summary>
    public static MapPicture Empty { get; } = new([], [], [], 0, 0);
}
