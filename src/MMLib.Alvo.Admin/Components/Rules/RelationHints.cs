using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Rules;

/// <summary>One relation term a rule on this entity may use, and the entity it reads.</summary>
/// <param name="Term">The term's shape, as it is typed: <c>customer_id.&lt;field&gt;</c> or <c>child.exists(x, …)</c>.</param>
/// <param name="Entity">The related entity whose rows the term reads.</param>
internal sealed record RelationHint(string Term, string Entity);

/// <summary>
/// The relation terms the rules editor offers under a rule: each reference this entity follows, and each child that
/// references it, spelled the way apply compiles them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hint, not a second compiler.</b> It reads the working copy's JSON for references only; the live check is what says
/// whether a typed term compiles. It offers what apply accepts as a target — a declared entity that is not the built-in
/// <c>users</c> and not <c>storage: dynamic</c> — so it never suggests a term the check would then refuse.
/// </para>
/// <para>
/// A child that references this entity more than once is offered only as <c>&lt;child&gt;_via_&lt;field&gt;</c>, one per
/// reference: the bare child name is ambiguous there, and apply refuses it.
/// </para>
/// </remarks>
internal static class RelationHints
{
    private const string UsersEntity = "users";

    /// <summary>The relation terms a rule on <paramref name="entity"/> may use, forward references first.</summary>
    /// <param name="descriptorJson">The working copy's text.</param>
    /// <param name="entity">The entity whose rules are being edited.</param>
    /// <returns>The terms, or none when the text is no descriptor or names no such entity.</returns>
    public static IReadOnlyList<RelationHint> For(string descriptorJson, string entity)
    {
        var readable = Readable(descriptorJson);
        return readable.TryGetValue(entity, out var declared)
            ? [.. Forward(declared, readable), .. Reverse(entity, readable)]
            : [];
    }

    private static IEnumerable<RelationHint> Forward(JsonObject entity, Dictionary<string, JsonObject> readable) =>
        from reference in References(entity)
        where readable.ContainsKey(reference.Target)
        select new RelationHint($"{reference.Field}.<field>", reference.Target);

    private static IEnumerable<RelationHint> Reverse(string entity, Dictionary<string, JsonObject> readable)
    {
        foreach (var (child, declared) in readable)
        {
            var references = References(declared).Where(reference => reference.Target == entity).ToList();
            var names = references.Count == 1
                ? [child]
                : references.Select(reference => $"{child}_via_{reference.Field}");
            foreach (var name in names)
            {
                yield return new RelationHint($"{name}.exists(x, x.<field> …)", child);
            }
        }
    }

    private static IEnumerable<(string Field, string Target)> References(JsonObject entity) =>
        from field in (entity["fields"] as JsonObject ?? []).AsEnumerable()
        where field.Value is JsonObject facets && Text(facets, "type") == "ref" && Text(facets, "entity") is not null
        select (field.Key, Text((JsonObject)field.Value!, "entity")!);

    /// <summary>The declared entities a relation may read, in declaration order.</summary>
    private static Dictionary<string, JsonObject> Readable(string descriptorJson)
    {
        var entities = Parse(descriptorJson)?["entities"] as JsonObject ?? [];
        return entities
            .Where(entity => entity.Key != UsersEntity && entity.Value is JsonObject declared && Text(declared, "storage") != "dynamic")
            .ToDictionary(entity => entity.Key, entity => (JsonObject)entity.Value!, StringComparer.Ordinal);
    }

    private static string? Text(JsonObject owner, string name) =>
        owner[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject? Parse(string json)
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
