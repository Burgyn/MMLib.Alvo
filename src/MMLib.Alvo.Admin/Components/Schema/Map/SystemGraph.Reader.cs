using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema.Map;

/* The JSON reading, kept apart from the shape it produces so SystemGraph.cs stays a record of what the
   map draws and this file stays a record of how the descriptor is read. */
internal sealed partial record SystemGraph
{
    private static readonly string[] _beforePoints = ["beforeCreate", "beforeUpdate", "beforeDelete"];
    private static readonly string[] _afterPoints = ["afterCreate", "afterUpdate", "afterDelete"];

    /// <summary>
    /// Reads a descriptor into the graph the system map draws.
    /// </summary>
    /// <remarks>
    /// Parses with <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/> inside a
    /// try that catches <see cref="JsonException"/> only, and every access below is guarded the same way
    /// <see cref="MMLib.Alvo.Admin.Internal.DescriptorLens"/> guards its own — a descriptor this screen did
    /// not write is still one the apply accepted, or one an operator has just pasted in and has not yet, and
    /// neither is a reason to throw.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor as stored — the working copy's own JSON.</param>
    /// <param name="appliedJson">
    /// The applied revision, to mark entities <see cref="MapEntity.Pending"/>; null when there is nothing to
    /// compare against, in which case nothing is pending.
    /// </param>
    public static SystemGraph From(string descriptorJson, string? appliedJson = null)
    {
        if (TryParseObject(descriptorJson) is not { } working || working["entities"] is not JsonObject declared)
        {
            return Empty;
        }

        var appliedEntities = appliedJson is null ? null : TryParseObject(appliedJson)?["entities"] as JsonObject;
        var entityNames = declared.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

        var entities = new List<MapEntity>();
        var edges = new List<MapEdge>();
        foreach (var pair in declared)
        {
            if (pair.Value is JsonObject entity)
            {
                entities.Add(ReadEntity(pair.Key, entity, entityNames, appliedJson is not null, appliedEntities, edges));
            }
        }

        var undrawn = ReadAutomation(working, entityNames, edges);

        return new SystemGraph(entities, ReadOutside(working, edges), edges, undrawn);
    }

    /// <summary>
    /// Reads one declared entity into its box, and — through <see cref="ReadField"/> and
    /// <see cref="ReadAfter"/> — the Reference and Hook edges its fields and after-hooks draw.
    /// </summary>
    /// <param name="name">The entity's name.</param>
    /// <param name="entity">The entity's declaration.</param>
    /// <param name="entityNames">Every declared entity name, so a ref field can test its target.</param>
    /// <param name="checkPending">Whether an applied revision was supplied at all.</param>
    /// <param name="appliedEntities">
    /// The applied revision's entities, or null when there is none, or it failed to parse.
    /// </param>
    /// <param name="edges">The edge list a ref field or after-hook appends to.</param>
    private static MapEntity ReadEntity(
        string name, JsonObject entity, IReadOnlySet<string> entityNames,
        bool checkPending, JsonObject? appliedEntities, List<MapEdge> edges)
    {
        var fields = ReadFields(entity, name, entityNames, edges);
        var (refuses, sets) = CountBefore(entity);
        ReadAfter(name, entity, edges);

        var scoped = StringOrNull(entity["tenancy"]) == "scoped";
        var pending = checkPending && IsPending(appliedEntities, name, entity);

        return new MapEntity(name, scoped, fields, refuses, sets, pending);
    }

    /// <summary>Every field an entity declares, in descriptor order.</summary>
    /// <param name="entity">The entity's declaration.</param>
    /// <param name="name">The entity's name, for a Reference edge's <c>From</c>.</param>
    /// <param name="entityNames">Every declared entity name, so a ref field can test its target.</param>
    /// <param name="edges">The edge list a ref field appends to.</param>
    private static List<MapField> ReadFields(
        JsonObject entity, string name, IReadOnlySet<string> entityNames, List<MapEdge> edges)
    {
        if (entity["fields"] is not JsonObject declared)
        {
            return [];
        }

        var fields = new List<MapField>();
        foreach (var pair in declared)
        {
            if (pair.Value is JsonObject field)
            {
                fields.Add(ReadField(pair.Key, field, name, entityNames, edges));
            }
        }

        return fields;
    }

    /// <summary>
    /// Reads one declared field, and — for a ref whose target entity is declared — the Reference edge
    /// it draws.
    /// </summary>
    /// <remarks>
    /// A ref to an undeclared entity keeps its row (the descriptor still names a field with a target) but
    /// loses its wire: a Reference edge with no box at the other end would draw a line to nowhere the map
    /// can place.
    /// </remarks>
    /// <param name="name">The field's name.</param>
    /// <param name="field">The field's declaration.</param>
    /// <param name="entityName">The entity this field belongs to, a Reference edge's <c>From</c>.</param>
    /// <param name="entityNames">Every declared entity name, to test a ref's target.</param>
    /// <param name="edges">The edge list a Reference field appends to.</param>
    private static MapField ReadField(
        string name, JsonObject field, string entityName, IReadOnlySet<string> entityNames, List<MapEdge> edges)
    {
        var type = StringOrNull(field["type"]);
        if (type == "ref" && StringOrNull(field["entity"]) is { } target)
        {
            var onDelete = StringOrNull(field["onDelete"]) ?? "restrict";
            if (entityNames.Contains(target))
            {
                edges.Add(new MapEdge(MapEdgeKind.Reference, entityName, target, onDelete, name, null));
            }

            return new MapField(name, $"→ {target} · {onDelete}", MapFieldKind.Reference);
        }

        return field["rollup"] is JsonObject rollup
            ? new MapField(name, RollupDetail(rollup), MapFieldKind.Rollup)
            : new MapField(name, type ?? string.Empty, MapFieldKind.Plain);
    }

    /// <summary>What a rollup field's box says it sums and where from.</summary>
    /// <param name="rollup">The field's <c>rollup</c> object.</param>
    private static string RollupDetail(JsonObject rollup)
    {
        var op = StringOrNull(rollup["op"]) ?? string.Empty;
        var from = StringOrNull(rollup["from"]) ?? string.Empty;
        return op == "count"
            ? $"Σ count from {from}"
            : $"Σ {op} {StringOrNull(rollup["field"])} from {from}";
    }

    /// <summary>Tallies before-hook actions across all three before points.</summary>
    /// <param name="entity">The entity's declaration.</param>
    /// <returns>How many entries reject, and how many mutate.</returns>
    private static (int Refuses, int Sets) CountBefore(JsonObject entity)
    {
        if (entity["hooks"] is not JsonObject hooks)
        {
            return (0, 0);
        }

        int refuses = 0, sets = 0;
        foreach (var entry in _beforePoints.SelectMany(point => hooks[point] as JsonArray ?? []))
        {
            if (entry is not JsonObject declared || declared["action"] is not JsonObject action)
            {
                continue;
            }

            refuses += action["reject"] is null ? 0 : 1;
            sets += action["mutate"] is null ? 0 : 1;
        }

        return (refuses, sets);
    }

    /// <summary>
    /// Reads one entity's after-hooks into Hook edges, one per email/webhook action, to the template or
    /// endpoint it uses.
    /// </summary>
    /// <param name="entityName">The entity the hook belongs to, the edge's <c>From</c>.</param>
    /// <param name="entity">The entity's declaration.</param>
    /// <param name="edges">The edge list to append to.</param>
    private static void ReadAfter(string entityName, JsonObject entity, List<MapEdge> edges)
    {
        if (entity["hooks"] is not JsonObject hooks)
        {
            return;
        }

        foreach (var point in _afterPoints)
        {
            foreach (var item in hooks[point] as JsonArray ?? [])
            {
                if (item is JsonObject entry && entry["action"] is JsonObject action && ActionTarget(action) is { } to)
                {
                    edges.Add(new MapEdge(MapEdgeKind.Hook, entityName, to, point, null, StringOrNull(entry["condition"])));
                }
            }
        }
    }

    /// <summary>
    /// Reads automation rules into Automation edges, one per email/webhook action of a rule whose trigger
    /// names a declared entity, and counts the rules that draw none.
    /// </summary>
    /// <remarks>
    /// A rule on <c>schedule</c>, on a pattern naming no single declared entity (<c>entity.*.created</c>, or an
    /// entity this descriptor does not declare), or whose actions are only <c>function</c>, <c>http.call</c> or
    /// <c>entity.update</c> draws no edge — there is no box for it to start at, or no node for it to reach. The
    /// count is returned so the map says so instead of dropping a declared rule without a word.
    /// </remarks>
    /// <param name="working">The descriptor root.</param>
    /// <param name="entityNames">Every declared entity name.</param>
    /// <param name="edges">The edge list to append to.</param>
    /// <returns>How many declared rules drew no edge.</returns>
    private static int ReadAutomation(JsonObject working, IReadOnlySet<string> entityNames, List<MapEdge> edges)
    {
        if (working["automation"] is not JsonObject rules)
        {
            return 0;
        }

        var undrawn = 0;
        foreach (var pair in rules)
        {
            if (pair.Value is JsonObject rule && !ReadRule(pair.Key, rule, entityNames, edges))
            {
                undrawn++;
            }
        }

        return undrawn;
    }

    /// <summary>Reads one automation rule into its Automation edges.</summary>
    /// <param name="name">The rule's name, the edge's <see cref="MapEdge.Rule"/>.</param>
    /// <param name="rule">The rule's declaration.</param>
    /// <param name="entityNames">Every declared entity name.</param>
    /// <param name="edges">The edge list to append to.</param>
    /// <returns>Whether the rule drew at least one edge.</returns>
    private static bool ReadRule(string name, JsonObject rule, IReadOnlySet<string> entityNames, List<MapEdge> edges)
    {
        if (TriggerEntity(rule, entityNames) is not { } source || rule["actions"] is not JsonArray actions)
        {
            return false;
        }

        var before = edges.Count;
        var condition = StringOrNull(rule["condition"]);
        foreach (var action in actions)
        {
            if (action is JsonObject declared && ActionTarget(declared) is { } to)
            {
                edges.Add(new MapEdge(MapEdgeKind.Automation, source, to, "automation · not yet", null, condition, name));
            }
        }

        return edges.Count > before;
    }

    /// <summary>
    /// The entity a rule's trigger names, or null when the trigger is not <c>entity.&lt;name&gt;.&lt;op&gt;</c>
    /// (or its coalesced <c>entity.&lt;name&gt;.&lt;op&gt;.batch</c> shape) for a declared entity.
    /// </summary>
    /// <remarks>
    /// The schema's <c>eventPattern</c> admits both shapes; a <c>.batch</c> rule still fires on that one
    /// entity's writes, only coalesced, so it starts at the same box.
    /// </remarks>
    /// <param name="rule">The automation rule.</param>
    /// <param name="entityNames">Every declared entity name.</param>
    private static string? TriggerEntity(JsonObject rule, IReadOnlySet<string> entityNames)
    {
        if (rule["trigger"] is not JsonObject trigger || StringOrNull(trigger["event"]) is not { } pattern)
        {
            return null;
        }

        var parts = pattern.Split('.');
        var shaped = parts.Length == 3 || (parts.Length == 4 && parts[3] == "batch");
        return shaped && parts[0] == "entity" && entityNames.Contains(parts[1]) ? parts[1] : null;
    }

    /// <summary>The outside id an email or webhook action names, or null for any other action type.</summary>
    /// <remarks>
    /// Two different reasons for the null. On an after-hook, any other type is refused at apply (before
    /// points take <c>reject</c>/<c>mutate</c>, after points take <c>email</c>/<c>webhook</c>), so one here is a
    /// malformed descriptor and is simply ignored. On an automation rule, <c>function</c>, <c>http.call</c> and
    /// <c>entity.update</c> are accepted — the block is warned, not refused — but none reaches a template or
    /// endpoint, so it has no node to draw a wire to; a rule left with no wire is counted in
    /// <see cref="Undrawn"/>.
    /// </remarks>
    /// <param name="action">The action object.</param>
    private static string? ActionTarget(JsonObject action) => StringOrNull(action["type"]) switch
    {
        "email" when StringOrNull(action["template"]) is { } template => $"template:{template}",
        "webhook" when StringOrNull(action["endpoint"]) is { } endpoint => $"endpoint:{endpoint}",
        _ => null,
    };

    /// <summary>
    /// Builds the outside nodes: every declared template and endpoint, in descriptor order, plus any id an
    /// edge reaches that neither declares — so the wire still has an end — in the order its edge was read.
    /// </summary>
    /// <remarks>
    /// <b>"Descriptor order" is the root object's own key order</b>, not a fixed <c>templates</c>-then-
    /// <c>webhooks</c> assumption: <c>examples/bike-workshop</c> declares <c>webhooks</c> before
    /// <c>templates</c>, <c>examples/complex-crm</c> the other way round, and a screen that always drew one
    /// block first would draw one of the two examples out of the order its own file puts them in.
    /// </remarks>
    /// <param name="working">The descriptor root.</param>
    /// <param name="edges">The edges already read; Hook and Automation edges name the outside ids in play.</param>
    private static List<MapOutside> ReadOutside(JsonObject working, IReadOnlyList<MapEdge> edges)
    {
        var used = edges.Where(edge => edge.Kind != MapEdgeKind.Reference)
            .Select(edge => edge.To)
            .ToHashSet(StringComparer.Ordinal);

        var outside = new List<MapOutside>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in working)
        {
            if (pair.Key == "templates" && pair.Value is JsonObject templates)
            {
                AddDeclared(outside, seen, templates, OutsideKind.Template, used);
            }
            else if (pair.Key == "webhooks" && pair.Value is JsonObject webhooks && webhooks["endpoints"] is JsonObject endpoints)
            {
                AddDeclared(outside, seen, endpoints, OutsideKind.Endpoint, used);
            }
        }

        AddUndeclared(outside, seen, edges);

        return outside;
    }

    /// <summary>Adds every name one declared block (<c>templates</c> or <c>webhooks.endpoints</c>) lists.</summary>
    /// <param name="outside">The node list to append to.</param>
    /// <param name="seen">The ids already added, so <see cref="AddUndeclared"/> does not repeat one.</param>
    /// <param name="declared">The block's declaration, or null when the descriptor does not have one.</param>
    /// <param name="kind">Whether this block declares templates or endpoints.</param>
    /// <param name="used">Every outside id at least one edge reaches.</param>
    private static void AddDeclared(
        List<MapOutside> outside, HashSet<string> seen, JsonObject? declared, OutsideKind kind, HashSet<string> used)
    {
        if (declared is null)
        {
            return;
        }

        foreach (var pair in declared)
        {
            var node = new MapOutside(pair.Key, kind, used.Contains(OutsideId(kind, pair.Key)));
            outside.Add(node);
            seen.Add(node.Id);
        }
    }

    /// <summary>The id a node of this kind and name would have — mirrors <see cref="MapOutside.Id"/>.</summary>
    /// <param name="kind">Whether this is a template or a webhook endpoint.</param>
    /// <param name="name">The template's or endpoint's name.</param>
    private static string OutsideId(OutsideKind kind, string name)
        => $"{(kind == OutsideKind.Template ? "template" : "endpoint")}:{name}";

    /// <summary>Adds a node for every outside id an edge reaches that no declared block named — used, always.</summary>
    /// <param name="outside">The node list to append to.</param>
    /// <param name="seen">The ids <see cref="AddDeclared"/> already added.</param>
    /// <param name="edges">The edges to scan, in read order.</param>
    private static void AddUndeclared(List<MapOutside> outside, HashSet<string> seen, IReadOnlyList<MapEdge> edges)
    {
        foreach (var edge in edges)
        {
            if (edge.Kind == MapEdgeKind.Reference || !seen.Add(edge.To))
            {
                continue;
            }

            var parts = edge.To.Split(':', 2);
            outside.Add(new MapOutside(parts[1], parts[0] == "template" ? OutsideKind.Template : OutsideKind.Endpoint, true));
        }
    }

    /// <summary>Whether an entity is absent from the applied revision, or differs from it.</summary>
    /// <param name="appliedEntities">The applied revision's entities, or null when there is none to compare against.</param>
    /// <param name="name">The entity's name.</param>
    /// <param name="entity">The working declaration to compare.</param>
    private static bool IsPending(JsonObject? appliedEntities, string name, JsonObject entity)
        => appliedEntities?[name] is not JsonObject applied || !JsonNode.DeepEquals(entity, applied);

    /// <summary>A JSON value's string, or null when it is missing or not a string.</summary>
    /// <param name="node">The node to read.</param>
    private static string? StringOrNull(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    /// <summary>
    /// Parses JSON into an object, or null on anything that fails to parse or is not an object at the root.
    /// </summary>
    /// <param name="json">The JSON text to parse.</param>
    private static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
