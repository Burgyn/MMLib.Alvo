using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Builds the candidate descriptor an expression input is checked against: the working copy's text with the draft
/// materialised in its slot, on a clone.
/// </summary>
/// <remarks>
/// <para>
/// The check endpoint refuses a path that is not in the descriptor it is sent (422), so the draft must already sit in
/// its slot. That is done on a fresh tree parsed from the text, never on the operator's
/// <see cref="WorkingCopy"/>: a check is a question, and asking it must not stage anything.
/// </para>
/// <para>
/// A slot that cannot be placed — an unknown entity, text that is not a descriptor — yields <see langword="null"/>:
/// there is nothing to check, so nothing is shown.
/// </para>
/// </remarks>
internal static class ExpressionSlots
{
    /// <summary>Places a rule's draft on a clone of the working copy.</summary>
    /// <param name="workingJson">The working copy's text; not modified.</param>
    /// <param name="entity">The entity the rule is on.</param>
    /// <param name="operation">The operation — list, get, create, update or delete.</param>
    /// <param name="source">The CEL as it stands in the box.</param>
    /// <returns>The clone's text and the slot's JSON Pointer, or <see langword="null"/> when there is no such entity.</returns>
    public static (string Json, string Path)? ForRule(string workingJson, string entity, string operation, string source)
    {
        if (Clone(workingJson) is not { } root || Entity(root, entity) is not { } declared)
        {
            return null;
        }

        var rules = declared["rules"] as JsonObject ?? (JsonObject)(declared["rules"] = new JsonObject());
        rules[operation] = source;

        return (root.ToJsonString(), Pointer("entities", entity, "rules", operation));
    }

    /// <summary>An RFC 6901 JSON Pointer to <paramref name="segments"/>.</summary>
    /// <param name="segments">The unescaped property names, outermost first.</param>
    /// <returns>The pointer, with <c>~</c> written <c>~0</c> and <c>/</c> written <c>~1</c>.</returns>
    public static string Pointer(params string[] segments)
        => string.Concat(segments.Select(segment => "/" + segment.Replace("~", "~0").Replace("/", "~1")));

    private static JsonObject? Clone(string json)
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

    private static JsonObject? Entity(JsonObject root, string entity)
        => (root["entities"] as JsonObject)?[entity] as JsonObject;
}
