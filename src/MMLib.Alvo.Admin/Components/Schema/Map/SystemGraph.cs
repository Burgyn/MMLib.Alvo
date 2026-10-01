namespace MMLib.Alvo.Admin.Components.Schema.Map;

/// <summary>What kind of value a field's <see cref="MapField.Detail"/> describes.</summary>
internal enum MapFieldKind
{
    /// <summary>An ordinary field: <see cref="MapField.Detail"/> is its declared type.</summary>
    Plain,

    /// <summary>A <c>ref</c> field: <see cref="MapField.Detail"/> names the entity and delete rule.</summary>
    Reference,

    /// <summary>A computed rollup: <see cref="MapField.Detail"/> names what it sums and where from.</summary>
    Rollup,
}

/// <summary>One declared field of an entity, as the map's box lists it.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Detail">
/// Plain: the type ("string"). Reference: "→ customers · restrict". Rollup: "Σ count from rentals" /
/// "Σ sum price from rentals".
/// </param>
/// <param name="Kind">What kind of field this is, so the map knows how to draw it.</param>
internal sealed record MapField(string Name, string Detail, MapFieldKind Kind);

/// <summary>One declared entity, as the map's box for it.</summary>
/// <param name="Name">The entity's name.</param>
/// <param name="Scoped">Whether the entity is tenant-scoped (<c>tenancy == "scoped"</c>).</param>
/// <param name="Fields">Every declared field in descriptor order.</param>
/// <param name="Refuses">How many before-hook entries have a <c>reject</c> action, over all three before points.</param>
/// <param name="Sets">How many before-hook entries have a <c>mutate</c> action.</param>
/// <param name="Pending">Absent from the applied descriptor, or its JSON differs from the applied one.</param>
internal sealed record MapEntity(string Name, bool Scoped, IReadOnlyList<MapField> Fields, int Refuses, int Sets, bool Pending);

/// <summary>What an outside node is — something a hook or automation action reaches, not an entity.</summary>
internal enum OutsideKind
{
    /// <summary>An email template, from <c>templates</c> or an action's <c>template</c>.</summary>
    Template,

    /// <summary>A webhook endpoint, from <c>webhooks.endpoints</c> or an action's <c>endpoint</c>.</summary>
    Endpoint,
}

/// <summary>A template or webhook endpoint the map draws as a node outside the entity graph.</summary>
/// <param name="Name">The template's or endpoint's name.</param>
/// <param name="Kind">Whether this is a template or a webhook endpoint.</param>
/// <param name="Used">Referenced by at least one hook or automation action.</param>
internal sealed record MapOutside(string Name, OutsideKind Kind, bool Used)
{
    /// <summary>"template:order-ready" / "endpoint:rental-desk" — the id an edge's <see cref="MapEdge.To"/> names.</summary>
    public string Id => $"{(Kind == OutsideKind.Template ? "template" : "endpoint")}:{Name}";
}

/// <summary>What kind of wire an edge draws.</summary>
internal enum MapEdgeKind
{
    /// <summary>A <c>ref</c> field, wired to its target entity.</summary>
    Reference,

    /// <summary>An after-hook action, wired to the template or endpoint it uses.</summary>
    Hook,

    /// <summary>An automation rule's action, wired to the template or endpoint it uses.</summary>
    Automation,
}

/// <summary>One wire the map draws, from an entity to whatever its field, hook or rule reaches.</summary>
/// <param name="Kind">Whether this is a ref field, an entity hook, or an automation rule.</param>
/// <param name="From">An entity name.</param>
/// <param name="To">Reference: the target entity name. Hook/Automation: a <see cref="MapOutside.Id"/>.</param>
/// <param name="Label">
/// Reference: onDelete ("restrict" when absent — the schema's default). Hook: the hook point
/// ("afterUpdate"). Automation: "automation · not yet".
/// </param>
/// <param name="Field">Reference: the ref field's name; otherwise null.</param>
/// <param name="Condition">The hook's or the rule's CEL <c>condition</c>, or null.</param>
/// <param name="Rule">Automation: the rule's name ("deal-won"), with or without a condition; otherwise null.</param>
internal sealed record MapEdge(
    MapEdgeKind Kind, string From, string To, string Label, string? Field, string? Condition, string? Rule = null);

/// <summary>
/// The descriptor read into what the system map draws: entities as boxes, refs/hooks/automation as wires
/// to other entities or to a template/endpoint outside the graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read, not modelled.</b> Like <see cref="MMLib.Alvo.Admin.Internal.DescriptorLens"/>, this reads one JSON document into
/// what one screen needs and nothing more — the map does not need, and so does not carry, most of what a
/// descriptor declares. The reader partial (<c>SystemGraph.Reader.cs</c>) holds the JSON reading; this file
/// holds the shape it produces and <see cref="Focus"/>, the one query the map runs against it.
/// </para>
/// <para>
/// <b>Never throws.</b> The screen this reads for renders whatever the descriptor happens to be — including
/// one nobody has applied yet, or one another tool wrote — so a malformed shape is skipped, never a reason
/// to blank the page.
/// </para>
/// </remarks>
/// <param name="Entities">Every declared entity, in descriptor order.</param>
/// <param name="Outside">Every template and webhook endpoint the graph reaches, in descriptor order.</param>
/// <param name="Edges">Every wire the map draws, in the order the descriptor was read.</param>
/// <param name="Undrawn">
/// How many declared automation rules draw no wire at all — they run on a <c>schedule</c>, match no single
/// declared entity (<c>entity.*.created</c>), or only call a <c>function</c>, an <c>http.call</c> or an
/// <c>entity.update</c>, none of which reaches a node this map draws. Counted so the map can say so, rather
/// than let a declared rule vanish from the picture without a word.
/// </param>
internal sealed partial record SystemGraph(
    IReadOnlyList<MapEntity> Entities, IReadOnlyList<MapOutside> Outside, IReadOnlyList<MapEdge> Edges, int Undrawn = 0)
{
    /// <summary>The graph of nothing — a descriptor that failed to parse, or declares no entities.</summary>
    public static SystemGraph Empty { get; } = new([], [], []);

    /// <summary>
    /// The centre, every entity one ref away in either direction, the outside nodes their hook/automation
    /// edges reach, and the edges among what is kept.
    /// </summary>
    /// <remarks>
    /// A reference edge is kept only when both ends survive the cut — a wire to an entity the focus dropped
    /// would point off the edge of the drawing. A hook or automation edge only needs its entity end to
    /// survive: its other end is never one of the entities being cut in the first place.
    /// <see cref="Undrawn"/> is kept whole: it is a fact about the descriptor's rules, most of which (a
    /// schedule) start at no entity a focus could keep or drop.
    /// </remarks>
    /// <param name="centre">The entity to focus the map on.</param>
    public SystemGraph Focus(string centre)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal) { centre };
        foreach (var edge in Edges.Where(edge => edge.Kind == MapEdgeKind.Reference))
        {
            if (edge.From == centre)
            {
                kept.Add(edge.To);
            }

            if (edge.To == centre)
            {
                kept.Add(edge.From);
            }
        }

        var entities = Entities.Where(entity => kept.Contains(entity.Name)).ToList();
        var edges = Edges
            .Where(edge => kept.Contains(edge.From) && (edge.Kind != MapEdgeKind.Reference || kept.Contains(edge.To)))
            .ToList();
        var outsideIds = edges.Where(edge => edge.Kind != MapEdgeKind.Reference)
            .Select(edge => edge.To)
            .ToHashSet(StringComparer.Ordinal);

        return new SystemGraph(entities, [.. Outside.Where(node => outsideIds.Contains(node.Id))], edges, Undrawn);
    }
}
