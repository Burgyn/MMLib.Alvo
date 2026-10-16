using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* An entity's events.exclude: the fields its event images leave out (spec 2026-10-14 §4.7, HX-01). */
internal sealed partial class WorkingCopy
{
    private const string EventsKey = "events";
    private const string ExcludeKey = "exclude";

    /// <summary>Whether the working copy leaves the field out of the entity's event images.</summary>
    public bool IsExcludedFromEvents(string entity, string field)
        => Read(() => ExclusionsOf(WorkingEntityOf(entity))?.Any(name => IsName(name, field)) is true);

    /// <summary>The fields the working copy leaves out of the entity's event images, in declared order.</summary>
    public IReadOnlyList<string> ExcludedFromEvents(string entity)
        => Read(() => ExclusionNames(WorkingEntityOf(entity)));

    /// <summary>Leaves the field out of the entity's event images, or puts it back; an empty block is removed, not kept.</summary>
    /// <remarks>Only a declared field is ever excluded: the apply refuses a name that is no field.</remarks>
    public void ExcludeFromEvents(string entity, string field, bool excluded) => Edit(_ =>
        WorkingEntityOf(entity) is { } declared && (excluded ? Exclude(declared, field) : Include(declared, field)));

    private static JsonArray? ExclusionsOf(JsonObject? declared) => declared?[EventsKey]?[ExcludeKey] as JsonArray;

    private static string[] ExclusionNames(JsonObject? declared)
        => [.. (ExclusionsOf(declared) ?? []).OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var name) ? name : null)
            .OfType<string>()];

    private static bool Exclude(JsonObject declared, string field)
    {
        if (declared["fields"]?[field] is null || ExclusionsOf(declared)?.Any(name => IsName(name, field)) is true)
        {
            return false;
        }

        var events = Ensure(declared, EventsKey);
        if (events[ExcludeKey] is not JsonArray names)
        {
            names = [];
            events[ExcludeKey] = names;
        }

        names.Add(field);
        return true;
    }

    private static bool Include(JsonObject declared, string field)
    {
        if (ExclusionsOf(declared) is not { } names || names.FirstOrDefault(name => IsName(name, field)) is not { } entry)
        {
            return false;
        }

        names.Remove(entry);
        DropIfEmpty(declared);
        return true;
    }

    private static void DropIfEmpty(JsonObject declared)
    {
        if (declared[EventsKey] is JsonObject events && ExclusionsOf(declared) is { Count: 0 })
        {
            events.Remove(ExcludeKey);
        }

        if (declared[EventsKey] is JsonObject { Count: 0 })
        {
            declared.Remove(EventsKey);
        }
    }

    private static bool IsName(JsonNode? node, string field)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && text == field;
}
