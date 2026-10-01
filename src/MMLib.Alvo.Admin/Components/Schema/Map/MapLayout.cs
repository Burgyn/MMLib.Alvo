using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Schema.Map;

/// <summary>
/// Places a <see cref="SystemGraph"/> into a <see cref="MapPicture"/>: a deterministic, left-to-right
/// layered layout the map draws as-is, with no interactive repositioning.
/// </summary>
/// <remarks>
/// <para>
/// <b>1. Layer.</b> <c>layer(e) = 0</c> when <c>e</c> has no Reference edge to another drawn entity;
/// else <c>1 + max(layer(target))</c> over its Reference targets. Computed with a visiting set so a
/// cycle (a→b→a) terminates — an edge back into the visiting set counts as 0 for that step.
/// Self-references never enter this computation.
/// </para>
/// <para>
/// <b>2. Order.</b> Each layer starts sorted by name (ordinal). Then four sweeps alternate: a
/// right-ward sweep (low layer to high) sorts layer <c>i</c> by the mean current index of its
/// Reference targets, which sit in lower, already-settled layers; a left-ward sweep (high to low)
/// sorts by the mean index of its Reference sources, in higher, already-settled layers. An entity
/// with no neighbours in that direction keeps its own index as its sort key, so it does not move
/// out from among entities that do have one. Ties break by current index, then name — the whole pass
/// is a pure function of the graph, so the same descriptor always lands the same picture.
/// </para>
/// <para>
/// <b>3. Coordinates.</b> A layer's boxes stack top to bottom with <see cref="BoxGap"/> between; a
/// box's own height depends only on what it shows, never on its neighbours.
/// </para>
/// <para>
/// <b>4. Deviation from the spec: no dummy nodes.</b> A layered layout conventionally threads a long
/// edge through an invisible node in every intervening layer, so no wire ever crosses a whole column.
/// This layout skips that — the graphs it draws are small enough (§0's 12-entity ceiling) that a
/// single Bézier straight across the intervening layer reads at least as clearly, and dummy nodes
/// would cost a second box kind the renderer has to special-case for no visible benefit here.
/// </para>
/// <para>
/// <b>5. Lanes for the outside wires.</b> The exception to 4 is a hook or automation wire whose entity is
/// not in the last column: a Bézier straight across to the outside column runs through the headers of every
/// column in between — under their plates if drawn first, striking through their names if drawn last — and
/// where it comes out again it reads as the last box's own wire (<c>examples/complex-crm</c>: <c>deals</c>'
/// automation looked like <c>invoice_items</c>'). So such a wire climbs, in the gap right of its box, to a
/// lane above every box, runs along it, and comes down in the gap before its node. Each crossing wire has
/// its own lane, <see cref="LaneStep"/> apart, and the whole picture moves down to make room for them.
/// </para>
/// </remarks>
internal static class MapLayout
{
    /// <summary>An entity box's fixed width, in layout units (px at the SVG's native scale).</summary>
    /// <remarks>
    /// Wide enough that a row holds 44 mono characters (<see cref="MapText.RowChars"/>) — a name such as
    /// <c>orders_assigned</c> beside <c>Σ count from service_orders</c> whole; longer rows are cut, detail first.
    /// </remarks>
    public const double BoxWidth = 320;

    /// <summary>The header band at the top of a box, above its first shown row.</summary>
    public const double Head = 42;

    /// <summary>One shown row's height, field or guard.</summary>
    public const double Row = 19;

    /// <summary>The blank band below a box's last row.</summary>
    public const double Pad = 12;

    /// <summary>The horizontal gap between one layer's right edge and the next layer's left edge.</summary>
    /// <remarks>Only a hook's or an automation's word sits in it — a reference says its delete rule on its row.</remarks>
    public const double LayerGap = 96;

    /// <summary>The vertical gap between two boxes (or outside nodes) stacked in the same column.</summary>
    public const double BoxGap = 38;

    /// <summary>An outside node's fixed width.</summary>
    public const double OutsideWidth = 208;

    /// <summary>An outside node's fixed height.</summary>
    public const double OutsideHeight = 44;

    /// <summary>The y of the first lane an outside wire rides above the boxes (see remark 5).</summary>
    public const double LaneTop = 10;

    /// <summary>The vertical step between two lanes, so two crossing wires do not merge into one.</summary>
    public const double LaneStep = 8;

    /// <summary>The clearance between the lowest lane and the top of every box and node.</summary>
    public const double LaneClear = 18;

    /// <summary>How many Plain fields a box shows before the rest fold into <see cref="PlacedBox.More"/>.</summary>
    public const int PlainRows = 6;

    /// <summary>Places every entity, wire and — when reactions are drawn — outside node in the graph.</summary>
    /// <param name="graph">The graph to lay out.</param>
    /// <param name="reactions">Whether the reactions layer is drawn: guard rows, the outside column, hook and automation wires.</param>
    public static MapPicture Arrange(SystemGraph graph, bool reactions)
    {
        if (graph.Entities.Count == 0)
        {
            return MapPicture.Empty;
        }

        var layer = Layers(graph);
        var byLayer = Order(graph, layer);
        var lanes = reactions ? Lanes(graph, layer) : new Dictionary<int, int>();
        var top = lanes.Count == 0 ? 0 : LaneTop + (lanes.Count - 1) * LaneStep + LaneClear;
        var boxes = Place(graph, byLayer, reactions, top);
        var outside = reactions
            ? PlaceOutside(graph, boxes, (layer.Values.Max() + 1) * (BoxWidth + LayerGap), top)
            : [];
        var wires = Wire(graph, boxes, outside, reactions, lanes);

        return new MapPicture(boxes, outside, wires, Extent(boxes, outside, box => box.X + BoxWidth, node => node.X + OutsideWidth),
            Extent(boxes, outside, box => box.Y + box.Height, node => node.Y + OutsideHeight));
    }

    /// <summary>
    /// The lane each crossing outside wire rides — keyed by the edge's index in <see cref="SystemGraph.Edges"/>,
    /// numbered in edge order: every hook or automation edge whose entity is not in the last column.
    /// </summary>
    /// <param name="graph">The graph being laid out.</param>
    /// <param name="layer">Every entity's layer, from <see cref="Layers"/>.</param>
    private static Dictionary<int, int> Lanes(SystemGraph graph, Dictionary<string, int> layer)
    {
        var last = layer.Values.Max();
        var lanes = new Dictionary<int, int>();
        for (var index = 0; index < graph.Edges.Count; index++)
        {
            var edge = graph.Edges[index];
            if (edge.Kind != MapEdgeKind.Reference && layer.TryGetValue(edge.From, out var from) && from < last)
            {
                lanes[index] = lanes.Count;
            }
        }

        return lanes;
    }

    /// <summary>Every entity's layer: 0 for a leaf, else one past the deepest entity it references.</summary>
    /// <param name="graph">The graph to layer.</param>
    private static Dictionary<string, int> Layers(SystemGraph graph)
    {
        var targets = graph.Edges.Where(e => e.Kind == MapEdgeKind.Reference && e.From != e.To)
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList(), StringComparer.Ordinal);

        var layer = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in graph.Entities)
        {
            LayerOf(entity.Name, targets, layer, []);
        }

        return layer;
    }

    /// <summary>One entity's layer, memoised, with a visiting set that terminates a reference cycle.</summary>
    /// <param name="name">The entity to compute the layer of.</param>
    /// <param name="targets">Every entity's Reference targets (self-references already excluded).</param>
    /// <param name="memo">Layers already settled.</param>
    /// <param name="visiting">Entities on the current recursion path — a back-edge into this set counts as 0.</param>
    private static int LayerOf(string name, IReadOnlyDictionary<string, List<string>> targets, Dictionary<string, int> memo, HashSet<string> visiting)
    {
        if (memo.TryGetValue(name, out var known))
        {
            return known;
        }

        if (!targets.TryGetValue(name, out var to) || to.Count == 0)
        {
            return memo[name] = 0;
        }

        visiting.Add(name);
        var deepest = to.Max(target => visiting.Contains(target) ? 0 : LayerOf(target, targets, memo, visiting));
        visiting.Remove(name);

        return memo[name] = 1 + deepest;
    }

    /// <summary>Every layer's entities, ordered by four barycentre sweeps over their Reference neighbours.</summary>
    /// <param name="graph">The graph being laid out.</param>
    /// <param name="layer">Every entity's layer, from <see cref="Layers"/>.</param>
    private static Dictionary<int, List<string>> Order(SystemGraph graph, Dictionary<string, int> layer)
    {
        var byLayer = graph.Entities.GroupBy(e => layer[e.Name])
            .ToDictionary(g => g.Key, g => g.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
        var maxLayer = byLayer.Keys.Max();

        for (var pass = 0; pass < 4; pass++)
        {
            var rightward = pass % 2 == 0;
            var order = rightward ? Enumerable.Range(0, maxLayer + 1) : Enumerable.Range(0, maxLayer + 1).Reverse();
            foreach (var i in order.Where(byLayer.ContainsKey))
            {
                Sweep(graph, IndexOf(byLayer), byLayer[i], rightward);
            }
        }

        return byLayer;
    }

    /// <summary>Every entity's current position within its own layer, across every layer.</summary>
    /// <param name="byLayer">The layout's current layer order.</param>
    private static Dictionary<string, int> IndexOf(Dictionary<int, List<string>> byLayer)
        => byLayer.OrderBy(kv => kv.Key)
            .SelectMany(kv => kv.Value.Select((name, index) => (name, index)))
            .ToDictionary(t => t.name, t => t.index, StringComparer.Ordinal);

    /// <summary>Re-sorts one layer's entities by the mean index of their neighbours in the swept direction.</summary>
    /// <param name="graph">The graph being laid out.</param>
    /// <param name="indexOf">Every entity's current index, from before this layer's sweep.</param>
    /// <param name="names">The layer's entities, in current order — sorted in place.</param>
    /// <param name="rightward">Whether targets (lower layers) or sources (higher layers) are the neighbours.</param>
    private static void Sweep(SystemGraph graph, IReadOnlyDictionary<string, int> indexOf, List<string> names, bool rightward)
    {
        var sorted = names.Select((name, index) => (name, index, key: SweepKey(graph, indexOf, name, index, rightward)))
            .OrderBy(t => t.key).ThenBy(t => t.index).ThenBy(t => t.name, StringComparer.Ordinal)
            .Select(t => t.name)
            .ToList();

        names.Clear();
        names.AddRange(sorted);
    }

    /// <summary>The sort key a sweep gives one entity: its neighbours' mean index, or its own when it has none.</summary>
    /// <param name="graph">The graph being laid out.</param>
    /// <param name="indexOf">Every entity's current index.</param>
    /// <param name="name">The entity being keyed.</param>
    /// <param name="index">The entity's own index before this sweep — its key when it has no neighbours.</param>
    /// <param name="rightward">Whether targets or sources count as the neighbours.</param>
    private static double SweepKey(SystemGraph graph, IReadOnlyDictionary<string, int> indexOf, string name, int index, bool rightward)
    {
        var neighbours = rightward
            ? graph.Edges.Where(e => e.Kind == MapEdgeKind.Reference && e.From == name && e.To != name).Select(e => e.To)
            : graph.Edges.Where(e => e.Kind == MapEdgeKind.Reference && e.To == name && e.From != name).Select(e => e.From);
        var indices = neighbours.Select(n => (double)indexOf[n]).ToList();

        return indices.Count > 0 ? indices.Average() : index;
    }

    /// <summary>Places every entity's box, stacked within its layer's column.</summary>
    /// <param name="graph">The graph being laid out.</param>
    /// <param name="byLayer">Every layer's entities, in final order.</param>
    /// <param name="reactions">Whether guard rows are drawn.</param>
    /// <param name="top">Where every column starts — below the lanes, when there are any.</param>
    private static List<PlacedBox> Place(SystemGraph graph, Dictionary<int, List<string>> byLayer, bool reactions, double top)
    {
        var entities = graph.Entities.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var boxes = new List<PlacedBox>();

        foreach (var i in byLayer.Keys.OrderBy(k => k))
        {
            var y = top;
            foreach (var name in byLayer[i])
            {
                var box = PlaceBox(entities[name], i, y, reactions);
                boxes.Add(box);
                y += box.Height + BoxGap;
            }
        }

        return boxes;
    }

    /// <summary>Places one entity's box: its shown rows, its height, and — when reactions are drawn — its guard row.</summary>
    /// <param name="entity">The entity to place.</param>
    /// <param name="layer">The entity's layer, its column index.</param>
    /// <param name="y">The box's top edge, from the running stack in <see cref="Place"/>.</param>
    /// <param name="reactions">Whether a guard row is drawn.</param>
    private static PlacedBox PlaceBox(MapEntity entity, int layer, double y, bool reactions)
    {
        var shown = entity.Fields.Where(f => f.Kind != MapFieldKind.Plain)
            .Concat(entity.Fields.Where(f => f.Kind == MapFieldKind.Plain).Take(PlainRows))
            .ToList();
        var more = Math.Max(0, entity.Fields.Count(f => f.Kind == MapFieldKind.Plain) - PlainRows);
        var guard = reactions ? Guard(entity) : null;
        var height = Head + (guard is null ? 0 : Row) + shown.Count * Row + Pad + (more > 0 ? 14 : 0);

        return new PlacedBox(entity, layer, layer * (BoxWidth + LayerGap), y, height, shown, more, guard);
    }

    /// <summary>An entity's guard row text, or null when it has no before-hooks.</summary>
    /// <param name="entity">The entity to describe.</param>
    private static string? Guard(MapEntity entity)
    {
        if (entity.Refuses == 0 && entity.Sets == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (entity.Refuses > 0)
        {
            parts.Add($"{entity.Refuses.ToString(CultureInfo.InvariantCulture)} refuse");
        }

        if (entity.Sets > 0)
        {
            parts.Add($"{entity.Sets.ToString(CultureInfo.InvariantCulture)} sets");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Places the outside column: every node with at least one wire, plus any declared-but-unused node
    /// (which has none), ordered by the mean row its wires start from — unused nodes last, by name.
    /// </summary>
    /// <param name="graph">The graph being laid out.</param>
    /// <param name="boxes">Every placed box, to read a wire's source row from.</param>
    /// <param name="x">The outside column's left edge, one layer gap past the rightmost box.</param>
    /// <param name="top">Where the column starts, level with the entity columns.</param>
    private static List<PlacedOutside> PlaceOutside(SystemGraph graph, IReadOnlyList<PlacedBox> boxes, double x, double top)
    {
        var boxByName = boxes.ToDictionary(b => b.Entity.Name, StringComparer.Ordinal);
        var wired = graph.Edges.Where(e => e.Kind != MapEdgeKind.Reference).ToLookup(e => e.To, StringComparer.Ordinal);

        var ordered = graph.Outside.Where(node => wired[node.Id].Any() || !node.Used)
            .OrderBy(node => OutsideKey(node, wired, boxByName))
            .ThenBy(node => node.Name, StringComparer.Ordinal)
            .ToList();

        var y = top;
        var placed = new List<PlacedOutside>();
        foreach (var node in ordered)
        {
            placed.Add(new PlacedOutside(node, x, y));
            y += OutsideHeight + BoxGap;
        }

        return placed;
    }

    /// <summary>An outside node's sort key: the mean <c>Y + 19</c> of the boxes its wires start from, or the maximum key when it has none.</summary>
    /// <param name="node">The outside node being keyed.</param>
    /// <param name="wired">Every hook/automation edge, by the outside id it reaches.</param>
    /// <param name="boxByName">Every placed box, by entity name.</param>
    private static double OutsideKey(MapOutside node, ILookup<string, MapEdge> wired, Dictionary<string, PlacedBox> boxByName)
    {
        var sources = wired[node.Id].Where(e => boxByName.ContainsKey(e.From)).Select(e => boxByName[e.From].Y + 19).ToList();

        return sources.Count > 0 ? sources.Average() : double.MaxValue;
    }

    /// <summary>Places every wire: a Reference from a field's row to its target, or — with reactions — a Hook/Automation to its outside node.</summary>
    /// <param name="graph">The graph being laid out.</param>
    /// <param name="boxes">Every placed box, by which a Reference or Hook/Automation wire is anchored.</param>
    /// <param name="outside">Every placed outside node, a Hook/Automation wire's far end.</param>
    /// <param name="reactions">Whether Hook/Automation wires are drawn at all.</param>
    /// <param name="lanes">The lane of every crossing outside wire, by edge index, from <see cref="Lanes"/>.</param>
    private static List<PlacedWire> Wire(
        SystemGraph graph, List<PlacedBox> boxes, List<PlacedOutside> outside, bool reactions, Dictionary<int, int> lanes)
    {
        var boxByName = boxes.ToDictionary(b => b.Entity.Name, StringComparer.Ordinal);
        var outsideById = outside.ToDictionary(o => o.Node.Id, StringComparer.Ordinal);
        var wires = new List<PlacedWire>();

        for (var index = 0; index < graph.Edges.Count; index++)
        {
            var edge = graph.Edges[index];
            if (edge.Kind == MapEdgeKind.Reference)
            {
                wires.Add(ReferenceWire(edge, boxByName));
            }
            else if (reactions && boxByName.TryGetValue(edge.From, out var source) && outsideById.TryGetValue(edge.To, out var node))
            {
                wires.Add(lanes.TryGetValue(index, out var lane) ? LaneWire(edge, source, node, lane) : OutsideWire(edge, source, node));
            }
        }

        return wires;
    }

    /// <summary>A Reference wire: from its field's row on the source box, to the target's header row — or, self-referencing, a loop back to its own.</summary>
    /// <param name="edge">The Reference edge to place.</param>
    /// <param name="boxByName">Every placed box, by entity name.</param>
    private static PlacedWire ReferenceWire(MapEdge edge, Dictionary<string, PlacedBox> boxByName)
    {
        var source = boxByName[edge.From];
        var rowIndex = source.Shown.ToList().FindIndex(f => f.Name == edge.Field);
        var (x1, y1) = (source.X, source.RowY(rowIndex < 0 ? 0 : rowIndex));

        if (edge.From == edge.To)
        {
            return new PlacedWire(edge, SelfCurve(x1, y1, source.Y + 19), x1, y1, x1, source.Y + 19);
        }

        var target = boxByName[edge.To];
        var (x2, y2) = (target.X + BoxWidth, target.Y + 19);
        return new PlacedWire(edge, Curve(x1, y1, x2, y2), x1, y1, x2, y2);
    }

    /// <summary>A Hook/Automation wire from the last column: from the entity's right edge straight to its outside node.</summary>
    /// <param name="edge">The Hook or Automation edge to place.</param>
    /// <param name="source">The placed box the edge starts at.</param>
    /// <param name="node">The placed outside node the edge reaches.</param>
    private static PlacedWire OutsideWire(MapEdge edge, PlacedBox source, PlacedOutside node)
    {
        var (x1, y1) = (source.X + BoxWidth, source.Y + 19);
        var (x2, y2) = (node.X, node.Y + OutsideHeight / 2);
        return new PlacedWire(edge, Curve(x1, y1, x2, y2), x1, y1, x2, y2);
    }

    /// <summary>
    /// A Hook/Automation wire from an earlier column: up to its lane in the gap right of its box, along the lane
    /// over every column in between, and down to its node in the gap before the outside column.
    /// </summary>
    /// <param name="edge">The Hook or Automation edge to place.</param>
    /// <param name="source">The placed box the edge starts at.</param>
    /// <param name="node">The placed outside node the edge reaches.</param>
    /// <param name="lane">The wire's lane number, from <see cref="Lanes"/>.</param>
    private static PlacedWire LaneWire(MapEdge edge, PlacedBox source, PlacedOutside node, int lane)
    {
        var (x1, y1) = (source.X + BoxWidth, source.Y + 19);
        var (x2, y2) = (node.X, node.Y + OutsideHeight / 2);
        var y = LaneTop + lane * LaneStep;
        var (up, down) = (x1 + LayerGap, x2 - LayerGap);
        var (m1, m2) = ((x1 + up) / 2, (down + x2) / 2);

        var path = $"M {Fmt(x1)} {Fmt(y1)} C {Fmt(m1)} {Fmt(y1)}, {Fmt(m1)} {Fmt(y)}, {Fmt(up)} {Fmt(y)} H {Fmt(down)}"
            + $" C {Fmt(m2)} {Fmt(y)}, {Fmt(m2)} {Fmt(y2)}, {Fmt(x2)} {Fmt(y2)}";
        return new PlacedWire(edge, path, x1, y1, x2, y2, y);
    }

    /// <summary>An S-curve between two points, its control points at their horizontal midpoint.</summary>
    /// <param name="x1">The start point's x.</param>
    /// <param name="y1">The start point's y.</param>
    /// <param name="x2">The end point's x.</param>
    /// <param name="y2">The end point's y.</param>
    private static string Curve(double x1, double y1, double x2, double y2)
    {
        var mid = (x1 + x2) / 2;
        return $"M {Fmt(x1)} {Fmt(y1)} C {Fmt(mid)} {Fmt(y1)}, {Fmt(mid)} {Fmt(y2)}, {Fmt(x2)} {Fmt(y2)}";
    }

    /// <summary>A self-reference's loop: out of the box's left edge and back into it, bulging left.</summary>
    /// <param name="x">The box's left edge, shared by both ends.</param>
    /// <param name="y">The loop's start, the referencing field's row.</param>
    /// <param name="y2">The loop's end, the box's own header row.</param>
    private static string SelfCurve(double x, double y, double y2)
    {
        var back = x - 44;
        return $"M {Fmt(x)} {Fmt(y)} C {Fmt(back)} {Fmt(y)}, {Fmt(back)} {Fmt(y2)}, {Fmt(x)} {Fmt(y2)}";
    }

    /// <summary>A layout number, formatted invariantly with at most one decimal place.</summary>
    /// <param name="value">The number to format.</param>
    private static string Fmt(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The furthest extent of every box and outside node along one axis, or 0 for none.</summary>
    /// <param name="boxes">Every placed box.</param>
    /// <param name="outside">Every placed outside node.</param>
    /// <param name="boxExtent">A box's far edge along the axis.</param>
    /// <param name="nodeExtent">An outside node's far edge along the axis.</param>
    private static double Extent(
        List<PlacedBox> boxes, List<PlacedOutside> outside, Func<PlacedBox, double> boxExtent, Func<PlacedOutside, double> nodeExtent)
        => Math.Max(boxes.Count > 0 ? boxes.Max(boxExtent) : 0, outside.Count > 0 ? outside.Max(nodeExtent) : 0);
}
