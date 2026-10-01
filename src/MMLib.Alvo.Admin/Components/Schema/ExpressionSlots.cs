using System.Globalization;
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

    /// <summary>Places the hook the add form would declare, with the typed condition, on a clone of the working copy.</summary>
    /// <remarks>
    /// The shape is the working copy's own <see cref="WorkingCopy.AddHook"/> run on a throwaway copy, so what is checked is
    /// exactly what Add would stage. The hook is appended, so it sits at the point's current length.
    /// </remarks>
    /// <param name="workingJson">The working copy's text; not modified.</param>
    /// <param name="entity">The entity the hook is on.</param>
    /// <param name="point">The hook point, <c>beforeCreate</c> through <c>afterDelete</c>.</param>
    /// <param name="action">The action the form would build (<see cref="HookBuilder.Draft"/>); not modified.</param>
    /// <param name="source">The CEL as it stands in the condition box.</param>
    /// <returns>The clone's text and the condition's JSON Pointer, or <see langword="null"/> when there is no such entity.</returns>
    public static (string Json, string Path)? ForHookCondition(
        string workingJson, string entity, string point, JsonObject action, string source)
        => WithHook(workingJson, entity, point, source, action) is { } slot
            ? (slot.Json, Pointer("entities", entity, "hooks", point, slot.Position, "condition"))
            : null;

    /// <summary>Places the hook the add form would declare, with the typed mutate value, on a clone of the working copy.</summary>
    /// <param name="workingJson">The working copy's text; not modified.</param>
    /// <param name="entity">The entity the hook is on.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="condition">The condition box's text, or empty for a hook that always runs.</param>
    /// <param name="action">The mutate action the form would build; not modified.</param>
    /// <param name="field">The field the mutate patches.</param>
    /// <param name="source">The CEL as it stands in the value box.</param>
    /// <returns>
    /// The clone's text and the value's JSON Pointer, or <see langword="null"/> when there is no such entity, no field has
    /// been named yet, or the action is not a mutate of it.
    /// </returns>
    public static (string Json, string Path)? ForMutateValue(
        string workingJson, string entity, string point, string? condition, JsonObject action, string field, string source)
    {
        if (string.IsNullOrWhiteSpace(field) || action.DeepClone() is not JsonObject patched
            || patched["mutate"]?[field] is not JsonObject value)
        {
            return null;
        }

        value["$cel"] = source;
        return WithHook(workingJson, entity, point, condition, patched) is { } slot
            ? (slot.Json, Pointer("entities", entity, "hooks", point, slot.Position, "action", "mutate", field))
            : null;
    }

    /// <summary>Places the field the add form would declare, with the typed expression, on a clone of the working copy.</summary>
    /// <remarks>
    /// The facets are what <c>FieldFacets.Build</c> made of the form, and the writer is the working copy's own
    /// <see cref="WorkingCopy.AddField"/>. The form's other refusals are not this check's to repeat: the caller passes
    /// nothing when the form cannot build a field yet.
    /// </remarks>
    /// <param name="workingJson">The working copy's text; not modified.</param>
    /// <param name="entity">The entity the field is added to.</param>
    /// <param name="field">The field's name.</param>
    /// <param name="facets">The facets the form built; not modified.</param>
    /// <param name="source">The CEL as it stands in the expression box.</param>
    /// <returns>The clone's text and the expression's JSON Pointer, or <see langword="null"/> when there is no such entity.</returns>
    public static (string Json, string Path)? ForComputed(
        string workingJson, string entity, string field, JsonObject facets, string source)
    {
        if (Scratch(workingJson, entity) is not { } scratch || facets.DeepClone() is not JsonObject declared)
        {
            return null;
        }

        declared["computed"] = source;
        scratch.AddField(entity, field, declared);
        return (scratch.Json, Pointer("entities", entity, "fields", field, "computed"));
    }

    /// <summary>An RFC 6901 JSON Pointer to <paramref name="segments"/>.</summary>
    /// <param name="segments">The unescaped property names, outermost first.</param>
    /// <returns>The pointer, with <c>~</c> written <c>~0</c> and <c>/</c> written <c>~1</c>.</returns>
    public static string Pointer(params string[] segments)
        => string.Concat(segments.Select(segment => "/" + segment.Replace("~", "~0").Replace("/", "~1")));

    private static (string Json, string Position)? WithHook(
        string workingJson, string entity, string point, string? condition, JsonObject action)
    {
        if (Scratch(workingJson, entity) is not { } scratch)
        {
            return null;
        }

        scratch.AddHook(entity, point, condition, (JsonObject)action.DeepClone());
        var json = scratch.Json;
        var count = ((JsonNode.Parse(json)!["entities"]![entity]!["hooks"]![point]) as JsonArray)!.Count;
        return (json, (count - 1).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A working copy of its own over the text, to run the real writers on: nothing outside it sees a change.</summary>
    private static WorkingCopy? Scratch(string workingJson, string entity)
    {
        if (Entity(Clone(workingJson) ?? [], entity) is null)
        {
            return null;
        }

        var scratch = new WorkingCopy();
        scratch.Take(workingJson, revision: 0);
        return scratch;
    }

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
