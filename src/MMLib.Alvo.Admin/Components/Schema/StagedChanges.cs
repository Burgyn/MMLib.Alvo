using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>How one declaration in the working copy differs from the applied revision.</summary>
internal enum StagedChange
{
    /// <summary>It is declared exactly as it is applied.</summary>
    None,

    /// <summary>The applied revision does not declare it.</summary>
    New,

    /// <summary>Both declare it, differently — a rename included.</summary>
    Changed,

    /// <summary>The applied revision declares it and the working copy no longer does.</summary>
    Removed,
}

/// <summary>One field of an entity, with what the working copy did to it.</summary>
/// <param name="Name">The field's name — in the working copy, or in the applied revision when it was removed.</param>
/// <param name="Change">What differs.</param>
internal sealed record StagedField(string Name, StagedChange Change);

/// <summary>
/// What the working copy changed, counted and located the way an operator thinks about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A unit is what an operator would name as one change</b> — an entity added, an entity changed, the
/// <c>access</c> block changed — not a line of the diff. Three fields staged on one entity are one entity
/// changed, and the count says so; a count of diff lines would tell the operator a number they could not
/// map back to anything they did.
/// </para>
/// <para>
/// <b>Compared as serialised text, the way <see cref="WorkingCopy.IsDirty"/> compares the whole
/// document.</b> A structural comparison would ignore key order, and then the bar and the preview could
/// disagree about whether anything is pending at all.
/// </para>
/// <para>
/// <b>A rename is matched through <c>renamedFrom</c>, so it is one change and not two.</b> The applied name
/// wins when both exist, because an applied descriptor keeps the key after the apply that consumed it.
/// </para>
/// </remarks>
internal static class StagedChanges
{
    /// <summary>How many top-level units differ between two descriptors.</summary>
    /// <param name="applied">The applied document.</param>
    /// <param name="working">The working document.</param>
    public static int Count(JsonNode? applied, JsonNode? working)
    {
        var before = applied as JsonObject;
        var after = working as JsonObject;

        return BlockChanges(before, after)
            + EntityChanges(before?["entities"] as JsonObject, after?["entities"] as JsonObject);
    }

    /// <summary>
    /// An entity's fields in the working copy's order, with the removed ones back where they were.
    /// </summary>
    /// <remarks>
    /// A removed field stays in the list rather than vanishing, because the Fields tab is where it was
    /// removed and where it can be restored — a row that disappears is a change the operator can only find
    /// again in the preview.
    /// </remarks>
    /// <param name="applied">The entity as applied, or <see langword="null"/> when it is new.</param>
    /// <param name="working">The entity as the working copy declares it.</param>
    public static IReadOnlyList<StagedField> Fields(JsonObject? applied, JsonObject? working)
    {
        var before = applied?["fields"] as JsonObject;
        var rows = Declared(before, working?["fields"] as JsonObject);

        InsertRemoved(rows, before);

        return [.. rows.Select(row => row.Field)];
    }

    /// <summary>
    /// The positions of a working list whose entry the applied list does not hold.
    /// </summary>
    /// <remarks>
    /// Matched by value and consumed once, because indexes and hooks are addressed by position and nothing
    /// forbids two identical entries — a second copy of an applied hook is a new hook.
    /// </remarks>
    /// <param name="applied">The list as applied.</param>
    /// <param name="working">The list as the working copy declares it.</param>
    public static IReadOnlySet<int> NewEntries(JsonArray? applied, JsonArray? working)
    {
        var pool = applied?.Select(Text).ToList() ?? [];
        var fresh = new HashSet<int>();

        for (var position = 0; position < (working?.Count ?? 0); position++)
        {
            if (!pool.Remove(Text(working![position])))
            {
                fresh.Add(position);
            }
        }

        return fresh;
    }

    /// <summary>
    /// The key a working declaration is applied under, or <see langword="null"/> when it is new.
    /// </summary>
    /// <param name="declared">The working declaration.</param>
    /// <param name="name">Its key in the working copy.</param>
    /// <param name="applied">The applied object it would be found in.</param>
    public static string? OriginOf(JsonNode? declared, string name, JsonObject? applied)
    {
        if (applied is null)
        {
            return null;
        }

        if (applied.ContainsKey(name))
        {
            return name;
        }

        return declared is JsonObject owner
            && owner["renamedFrom"] is JsonValue value
            && value.TryGetValue<string>(out var origin)
            && applied.ContainsKey(origin)
                ? origin
                : null;
    }

    /// <summary>Every top-level block other than <c>entities</c> that differs, one each.</summary>
    private static int BlockChanges(JsonObject? before, JsonObject? after)
        => Keys(before).Union(Keys(after), StringComparer.Ordinal)
            .Where(key => !string.Equals(key, "entities", StringComparison.Ordinal))
            .Count(key => !Same(before?[key], after?[key]));

    /// <summary>Entities added, changed or removed, one each.</summary>
    private static int EntityChanges(JsonObject? before, JsonObject? after)
    {
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var changes = 0;

        foreach (var (name, declared) in after ?? [])
        {
            var origin = OriginOf(declared, name, before);
            if (origin is null || !Same(before![origin], declared))
            {
                changes++;
            }

            if (origin is not null)
            {
                matched.Add(origin);
            }
        }

        return changes + Keys(before).Count(name => !matched.Contains(name));
    }

    /// <summary>The fields the working copy declares, each with its change and the applied name it came from.</summary>
    private static List<Row> Declared(JsonObject? before, JsonObject? after)
    {
        var rows = new List<Row>();

        foreach (var (name, declared) in after ?? [])
        {
            var origin = OriginOf(declared, name, before);
            var change = origin is null ? StagedChange.New
                : Same(before![origin], declared) ? StagedChange.None
                : StagedChange.Changed;

            rows.Add(new Row(new StagedField(name, change), origin));
        }

        return rows;
    }

    /// <summary>Puts each applied field the working copy dropped back after the field it followed.</summary>
    private static void InsertRemoved(List<Row> rows, JsonObject? before)
    {
        var order = Keys(before).ToList();
        var kept = rows.Select(row => row.Origin).OfType<string>().ToHashSet(StringComparer.Ordinal);

        foreach (var removed in order.Where(name => !kept.Contains(name)))
        {
            rows.Insert(
                PositionFor(removed, order, rows),
                new Row(new StagedField(removed, StagedChange.Removed), removed));
        }
    }

    /// <summary>Just after the last row whose applied name came before this one, or first.</summary>
    private static int PositionFor(string removed, List<string> order, List<Row> rows)
    {
        var earlier = order.Take(order.IndexOf(removed)).ToHashSet(StringComparer.Ordinal);

        return rows.FindLastIndex(row => row.Origin is { } origin && earlier.Contains(origin)) + 1;
    }

    private static IEnumerable<string> Keys(JsonObject? owner) => owner?.Select(pair => pair.Key) ?? [];

    private static bool Same(JsonNode? before, JsonNode? after)
        => string.Equals(Text(before), Text(after), StringComparison.Ordinal);

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    /// <summary>A field row, with the applied name it is matched against.</summary>
    private sealed record Row(StagedField Field, string? Origin);
}
