using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Whether the hook editor can draw a declared hook — and so rewrite it without losing anything — or must leave it as it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The edit-in-place half of <c>A_hook_the_editor_cannot_draw_survives_beside_it</c></b> (spec §5.2, ruling B1). A writer
/// that rebuilt a hook it cannot draw would drop what it does not know — an email's <c>data</c>, a refused action type, an
/// <c>x-</c> key — which is the silent narrowing <see cref="WorkingCopy"/> exists against. Such a hook is read-only: its row
/// keeps Remove and says why it has no Edit.
/// </para>
/// <para>
/// Every read-only shape is one the apply refuses or the schema rejects; it reaches a working copy through an import or the
/// assistant, never through this editor.
/// </para>
/// </remarks>
internal static class HookShape
{
    private static readonly HashSet<string> _hookKeys = new(StringComparer.Ordinal) { "condition", "action" };
    private static readonly HashSet<string> _webhookKeys = new(StringComparer.Ordinal) { "type", "endpoint", "payload" };
    private static readonly HashSet<string> _emailKeys = new(StringComparer.Ordinal) { "type", "template", "to" };
    private static readonly HashSet<string> _refusedTypes = new(StringComparer.Ordinal) { "function", "http.call", "entity.update" };

    /// <summary>Why the editor cannot draw this hook, or <see langword="null"/> when it can.</summary>
    /// <param name="hook">The declared hook.</param>
    /// <param name="point">The point it is declared at.</param>
    /// <returns>One sentence, or <see langword="null"/>.</returns>
    public static string? Undrawable(JsonNode? hook, string point)
    {
        if (hook is not JsonObject declared)
        {
            return "It is not an object.";
        }

        if (Unknown(declared, _hookKeys) is { } keys)
        {
            return $"It carries keys the editor does not know: {keys}.";
        }

        if (declared.TryGetPropertyValue("condition", out var condition) && !IsString(condition))
        {
            return "Its condition is not a string.";
        }

        if (declared["action"] is not JsonObject action)
        {
            return "Its action is not an object.";
        }

        return HookBuilder.IsBefore(point) ? BeforeAction(action, point) : AfterAction(action);
    }

    private static string? BeforeAction(JsonObject action, string point)
    {
        if (action.Count == 1 && action.TryGetPropertyValue("reject", out var reject))
        {
            return IsString(reject) ? null : "Its reject message is not a string.";
        }

        if (action.Count != 1 || action["mutate"] is not JsonObject patch)
        {
            return "Its action is neither a reject nor a mutate, the two a before-hook may take.";
        }

        if (point == "beforeDelete")
        {
            return "A mutate under beforeDelete is refused by this build: the row is being removed.";
        }

        return patch.Count == 0 ? "Its mutate patches no field." : patch.Select(MutateValue).FirstOrDefault(reason => reason is not null);
    }

    private static string? MutateValue(KeyValuePair<string, JsonNode?> pair) => pair.Value switch
    {
        null => null,
        JsonObject tagged when tagged.Count == 1 && IsString(tagged["$cel"]) => null,
        JsonObject or JsonArray => $"The mutate value for '{pair.Key}' is a JSON object or array, which the editor cannot draw.",
        _ => null,
    };

    private static string? AfterAction(JsonObject action)
    {
        if (action["type"] is not JsonValue value || !value.TryGetValue<string>(out var type))
        {
            return "Its action has no type.";
        }

        return type switch
        {
            HookBuilder.Webhook => Keys(action, _webhookKeys) ?? Required(action, "endpoint") ?? Optional(action, "payload"),
            HookBuilder.Email when action.ContainsKey("data") => "It carries 'data', which this build refuses (email.data).",
            HookBuilder.Email => Keys(action, _emailKeys) ?? Required(action, "template", "to"),
            _ when _refusedTypes.Contains(type) => $"Its action type '{type}' is refused by this build.",
            _ => $"Its action type '{type}' is not one the schema declares.",
        };
    }

    private static string? Keys(JsonObject action, HashSet<string> allowed)
        => Unknown(action, allowed) is { } keys ? $"Its action carries keys the editor does not know: {keys}." : null;

    private static string? Unknown(JsonObject owner, HashSet<string> allowed)
    {
        var unknown = owner.Select(pair => pair.Key).Where(key => !allowed.Contains(key)).ToList();
        return unknown.Count == 0 ? null : string.Join(", ", unknown);
    }

    private static string? Required(JsonObject action, params string[] keys)
        => keys.FirstOrDefault(key => !IsString(action[key])) is { } missing ? $"Its '{missing}' is missing or not a string." : null;

    private static string? Optional(JsonObject action, string key)
        => action.TryGetPropertyValue(key, out var value) && !IsString(value) ? $"Its '{key}' is not a string." : null;

    private static bool IsString(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String;
}
