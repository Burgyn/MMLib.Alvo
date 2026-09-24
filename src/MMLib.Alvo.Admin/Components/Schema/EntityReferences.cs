using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Every place a descriptor names an entity by its key, so a rename can carry them along with the key.
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
            foreach (var field in entities.Select(entity => entity.Value?["fields"]).OfType<JsonObject>()
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

    /// <summary>The objects under a node, taken before anything is rewritten, and never a payload's.</summary>
    private static List<JsonObject> Children(JsonObject node)
        => [.. node.Where(pair => !string.Equals(pair.Key, "payload", StringComparison.Ordinal))
            .SelectMany(pair => pair.Value is JsonArray array ? (IEnumerable<JsonNode?>)array : [pair.Value])
            .OfType<JsonObject>()];

    private static bool Is(JsonNode? node, string expected)
        => node is JsonValue value && value.TryGetValue<string>(out var text)
            && string.Equals(text, expected, StringComparison.Ordinal);
}
