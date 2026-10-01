using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Every place a descriptor names an entity by its key, so a rename can carry them along with the key and a
/// removal can name them first.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rename that left these behind was a plan the apply refuses.</b> Renaming <c>regions</c> moved the key
/// and kept <c>work_orders.region_id</c> pointing at <c>regions</c>, which no longer existed: the validator
/// answered "Field references unknown entity", and the operator was left to find and edit each reference by
/// hand.
/// </para>
/// <para>
/// <b>The places, from the frozen schema</b> (<c>schema/project.schema.json</c>): a ref field's <c>entity</c>, a
/// rollup's <c>from</c> (its <c>via</c> names a field, not an entity), an <c>entity.update</c> action's
/// <c>entity</c>, and the entity segment of a trigger's <c>event</c> pattern (<c>entity.regions.updated</c>).
/// A CEL expression is not rewritten: an identifier in one is not known to be the entity, and a rewrite that
/// guessed would change an expression nobody asked to change. An action's <c>payload</c> is not entered, because
/// its values are data written to a record, not names in the descriptor.
/// </para>
/// </remarks>
internal static class EntityReferences
{
    /// <summary>Points every reference to <paramref name="from"/> at <paramref name="to"/>.</summary>
    /// <param name="root">The working document.</param>
    /// <param name="from">The entity's old name.</param>
    /// <param name="to">Its new name.</param>
    public static void Rename(JsonObject root, string from, string to)
    {
        if (root["entities"] is JsonObject entities)
        {
            foreach (var field in entities.Where(entity => Enters(entity.Key)).Select(entity => entity.Value?["fields"]).OfType<JsonObject>()
                         .SelectMany(fields => fields.Select(pair => pair.Value)).OfType<JsonObject>())
            {
                RenameInField(field, from, to);
            }
        }

        RenameInActionsAndTriggers(root, from, to);
    }

    private static void RenameInField(JsonObject field, string from, string to)
    {
        if (Is(field["type"], "ref") && Is(field["entity"], from))
        {
            field["entity"] = to;
        }

        if (field["rollup"] is JsonObject rollup && Is(rollup["from"], from))
        {
            rollup["from"] = to;
        }
    }

    /// <summary>Walks the whole document for actions and trigger patterns, which live in several blocks.</summary>
    private static void RenameInActionsAndTriggers(JsonObject node, string from, string to)
    {
        if (Is(node["type"], "entity.update") && Is(node["entity"], from))
        {
            node["entity"] = to;
        }

        var prefix = $"entity.{from}.";
        if (node["event"] is JsonValue value && value.TryGetValue<string>(out var pattern)
            && pattern.StartsWith(prefix, StringComparison.Ordinal))
        {
            node["event"] = $"entity.{to}.{pattern[prefix.Length..]}";
        }

        foreach (var child in Children(node))
        {
            RenameInActionsAndTriggers(child, from, to);
        }
    }

    /// <summary>
    /// The objects under a node, taken before anything is rewritten, and never a payload's, a <c>mutate</c>'s or an
    /// <c>x-*</c> extension's: those hold data and free-form annotations, not names in the descriptor.
    /// </summary>
    private static List<JsonObject> Children(JsonObject node)
        => [.. node.Where(pair => Enters(pair.Key))
            .SelectMany(pair => pair.Value is JsonArray array ? (IEnumerable<JsonNode?>)array : [pair.Value])
            .OfType<JsonObject>()];

    /// <summary>
    /// Whether a walk for names goes under <paramref name="key"/>: not into an action's <c>payload</c> or a before-hook's
    /// <c>mutate</c> (values written to a record, where an object shaped like an action is a literal), nor into an
    /// <c>x-*</c> extension (the schema's free-form annotations, which the build never reads).
    /// </summary>
    /// <param name="key">The key the child sits under.</param>
    private static bool Enters(string key)
        => !string.Equals(key, "payload", StringComparison.Ordinal)
            && !string.Equals(key, "mutate", StringComparison.Ordinal)
            && !key.StartsWith("x-", StringComparison.Ordinal);

    /// <summary>
    /// Every place outside the entity that names it, each with what becomes of it; a place blocks the removal only when
    /// the apply itself would refuse the descriptor left behind for it — a ref (<c>DescriptorValidator.cs:419</c>) or a
    /// rollup's <c>from</c> (<c>RollupResolver.cs:186</c>).
    /// </summary>
    /// <remarks>
    /// <para>What the entity declares about itself — a self-ref, its own hooks — goes with it and is not named.</para>
    /// <para>
    /// <b>Named, and not blocking</b>, because nothing in this build refuses them for naming a missing entity:
    /// an <c>entity.update</c> action in a hook is refused by the after-hook compiler whatever it names
    /// (<c>AfterHookCompiler.RefuseAction</c>, <c>UnhonouredFeatures.UnhonouredAction</c>), so the removal adds no
    /// refusal; one in an automation rule, and every trigger pattern, is never compiled — the <c>automation</c> and
    /// <c>functions</c> blocks are only warned about as a whole (<c>UnhonouredSubsystems.All</c>).
    /// </para>
    /// </remarks>
    /// <param name="root">The working document.</param>
    /// <param name="entity">The entity to be removed.</param>
    public static IReadOnlyList<DescriptorReference> Inbound(JsonObject root, string entity)
    {
        var found = new List<DescriptorReference>();
        if (root["entities"] is JsonObject entities)
        {
            foreach (var (owner, node) in entities.Where(pair => pair.Key != entity && Enters(pair.Key)))
            {
                InboundFields(owner, node?["fields"] as JsonObject, entity, found);
            }
        }

        InboundActions(root, string.Empty, entity, found);
        return found;
    }

    /// <summary>The ref fields and rollups of one other entity that name <paramref name="entity"/>.</summary>
    /// <param name="owner">The entity whose fields these are.</param>
    /// <param name="fields">Its fields block, when it has one.</param>
    /// <param name="entity">The entity to be removed.</param>
    /// <param name="found">Where each place is added.</param>
    private static void InboundFields(string owner, JsonObject? fields, string entity, List<DescriptorReference> found)
    {
        foreach (var (name, node) in fields ?? [])
        {
            if (Is(node?["type"], "ref") && Is(node?["entity"], entity))
            {
                found.Add(new($"{owner}.{name}", Blocks: true, EntityRemovalWords.Ref));
            }

            if (node?["rollup"] is JsonObject rollup && Is(rollup["from"], entity))
            {
                found.Add(new($"{owner}.{name} rollup", Blocks: true, EntityRemovalWords.Rollup));
            }
        }
    }

    /// <summary>
    /// Walks the whole document for <c>entity.update</c> actions and trigger patterns naming the entity, past its own
    /// declaration and what <see cref="Enters"/> keeps out (<see cref="RenameInActionsAndTriggers"/>'s places, read
    /// instead of rewritten).
    /// </summary>
    /// <param name="node">The object being read.</param>
    /// <param name="path">Its dotted path from the root, which is the place an operator is shown.</param>
    /// <param name="entity">The entity to be removed.</param>
    /// <param name="found">Where each place is added.</param>
    private static void InboundActions(JsonObject node, string path, string entity, List<DescriptorReference> found)
    {
        if (Is(node["type"], "entity.update") && Is(node["entity"], entity))
        {
            found.Add(new(path, Blocks: false, EntityRemovalWords.Action(path)));
        }

        if (node["event"] is JsonValue value && value.TryGetValue<string>(out var pattern)
            && pattern.StartsWith($"entity.{entity}.", StringComparison.Ordinal))
        {
            found.Add(new(path, Blocks: false, EntityRemovalWords.Trigger(path)));
        }

        foreach (var (key, child) in node.Where(pair => Enters(pair.Key) && !(path == "entities" && pair.Key == entity)))
        {
            foreach (var (at, item) in Items(key, child))
            {
                InboundActions(item, path.Length == 0 ? at : $"{path}.{at}", entity, found);
            }
        }
    }

    /// <summary>The objects under one key, each with the path segment it is found at: <c>key</c> or <c>key[i]</c>.</summary>
    /// <param name="key">The key the child sits under.</param>
    /// <param name="child">The child: an object, an array of them, or a value, which holds none.</param>
    private static IEnumerable<(string At, JsonObject Item)> Items(string key, JsonNode? child) => child switch
    {
        JsonObject single => [(key, single)],
        JsonArray list => list.Select((item, i) => (At: string.Create(CultureInfo.InvariantCulture, $"{key}[{i}]"), Item: item as JsonObject))
            .Where(pair => pair.Item is not null).Select(pair => (pair.At, pair.Item!)),
        _ => [],
    };

    private static bool Is(JsonNode? node, string expected)
        => node is JsonValue value && value.TryGetValue<string>(out var text)
            && string.Equals(text, expected, StringComparison.Ordinal);
}
