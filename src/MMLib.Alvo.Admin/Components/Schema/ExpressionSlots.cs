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

    /// <summary>Places the field the add form would declare, with the typed expression, on a clone of the working copy.</summary>
    /// <remarks>
    /// The facets are what <c>FieldFacets.Build</c> made of the form, and the writer is the working copy's own
    /// <see cref="WorkingCopy.SaveField"/> — the one Save runs, so an edited field that is renamed loses its old key
    /// (and the references to it are carried) here exactly as it would there. The form's other refusals are not this
    /// check's to repeat: the caller passes nothing when the form cannot build a field yet.
    /// </remarks>
    /// <param name="workingJson">The working copy's text; not modified.</param>
    /// <param name="entity">The entity the field is added to.</param>
    /// <param name="editing">The name of the field being edited, or <see langword="null"/> for a new one.</param>
    /// <param name="field">The field's name.</param>
    /// <param name="facets">The facets the form built; not modified.</param>
    /// <param name="source">The CEL as it stands in the expression box.</param>
    /// <returns>The clone's text and the expression's JSON Pointer, or <see langword="null"/> when there is no such entity.</returns>
    public static (string Json, string Path)? ForComputed(
        string workingJson, string entity, string? editing, string field, JsonObject facets, string source)
    {
        if (Scratch(workingJson, entity) is not { } scratch || facets.DeepClone() is not JsonObject declared)
        {
            return null;
        }

        declared["computed"] = source;
        scratch.SaveField(entity, editing, field, declared);
        return (scratch.Json, Pointer("entities", entity, "fields", field, "computed"));
    }

    /// <summary>Places a whole hook — the one the editor would write, with the typed text already in its slot — on a clone.</summary>
    /// <remarks>
    /// Position-aware: an edited hook is checked where it sits, so a value is judged in the place Save would put it; a
    /// new one is appended, as Add would append it. The hook is the editor's own candidate, so what is checked is what
    /// Add or Save would stage; whether it carries the condition is the caller's choice (slice B design D4: payload, to
    /// and mutate values are checked without it).
    /// </remarks>
    /// <param name="workingJson">The working copy's text; not modified.</param>
    /// <param name="entity">The entity the hook is on.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="position">The edited hook's position, or <see langword="null"/> to append.</param>
    /// <param name="hook">The hook; not modified.</param>
    /// <param name="slot">The slot inside the hook, outermost first: <c>condition</c>, or <c>action</c>, <c>payload</c>.</param>
    /// <returns>The clone's text and the slot's pointer, or <see langword="null"/> when there is no such entity or position.</returns>
    public static (string Json, string Path)? ForHook(
        string workingJson, string entity, string point, int? position, JsonObject hook, params string[] slot)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (Clone(workingJson) is not { } root || Entity(root, entity) is not { } declared)
        {
            return null;
        }

        var hooks = declared["hooks"] as JsonObject ?? (JsonObject)(declared["hooks"] = new JsonObject());
        var list = hooks[point] as JsonArray ?? (JsonArray)(hooks[point] = new JsonArray());
        var at = Place(list, position, (JsonObject)hook.DeepClone());
        return at < 0
            ? null
            : (root.ToJsonString(), Pointer([.. new[] { "entities", entity, "hooks", point, at.ToString(CultureInfo.InvariantCulture) }, .. slot]));
    }

    /// <summary>An RFC 6901 JSON Pointer to <paramref name="segments"/>.</summary>
    /// <param name="segments">The unescaped property names, outermost first.</param>
    /// <returns>The pointer, with <c>~</c> written <c>~0</c> and <c>/</c> written <c>~1</c>.</returns>
    public static string Pointer(params string[] segments)
        => string.Concat(segments.Select(segment => "/" + segment.Replace("~", "~0").Replace("/", "~1")));

    /// <summary>Appends the hook, or puts it at its position; the index it landed at, or -1 when nothing is there.</summary>
    private static int Place(JsonArray list, int? position, JsonObject hook)
    {
        if (position is not { } at)
        {
            list.Add(hook);
            return list.Count - 1;
        }

        if (at < 0 || at >= list.Count)
        {
            return -1;
        }

        list[at] = hook;
        return at;
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
