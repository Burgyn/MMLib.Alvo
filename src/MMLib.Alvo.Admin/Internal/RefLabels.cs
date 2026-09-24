using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Which fields name a row, and turning the ids in a reference column into those names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The label is a heuristic, not a descriptor key — the refined §4.6 heuristic.</b> The schema has no
/// <c>displayField</c>, and inventing one is a schema change outside this pass (design pass §4.1). The rule
/// is deterministic, so a later <c>displayField</c> replaces it without the screens noticing:
/// </para>
/// <list type="number">
/// <item>a string field with a name-like name, in the order of <see cref="NameLike"/>, then any field
/// ending in <c>_number</c> (<c>order_number</c>);</item>
/// <item><c>first_name</c> and <c>last_name</c> together, as "first last";</item>
/// <item>the first required string, then the first string;</item>
/// <item>nothing — the cell shows the short id.</item>
/// </list>
/// <para>
/// §4.6 as first written was only step 3, and it named bikes by their brand and customers by their first
/// name alone: a label that is true of many rows is not a label. A field hidden from every caller is never
/// a candidate; one masked by CEL is, because it comes back for some callers.
/// </para>
/// <para>
/// <b>One query per reference column per page, never one per cell.</b> The distinct ids on the page
/// go to the target in a single <c>in</c> filter, with a projection of the id and the label's fields
/// only — twenty-five rows of a grid cost one round trip per column rather than twenty-five, and the
/// read carries nothing the cell does not draw. The list is split at
/// <see cref="AlvoFilter.MaxInCandidates"/>, because a longer one is refused as a malformed query.
/// </para>
/// <para>
/// <b>It goes through the data port as the operator</b>, like every other read on the Data screen, so
/// a label is only ever a value this caller could have listed through <c>/api</c>. A row their rule
/// excludes, a label field masked from them, or a target they hold no tenant for all end the same
/// way: no label, and the cell shows the short id.
/// </para>
/// </remarks>
internal static class RefLabels
{
    /// <summary>The field names that name a row, most name-like first.</summary>
    public static IReadOnlyList<string> NameLike { get; } =
        ["name", "title", "label", "display_name", "reference", "code", "sku"];

    private const string NumberSuffix = "_number";

    /// <summary>How many characters of a uuid stand for it when there is no label.</summary>
    public const int ShortIdLength = 8;

    /// <summary>The first characters of <paramref name="id"/>.</summary>
    public static string ShortId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture)[..ShortIdLength];

    /// <summary>A stored reference value as a uuid, when it is one.</summary>
    public static Guid? IdOf(object? value) => value switch
    {
        Guid id => id,
        string text when Guid.TryParse(text, out var id) => id,
        _ => null,
    };

    /// <summary>A label value as text; <see langword="null"/> when there is nothing to show.</summary>
    /// <remarks>
    /// A masked field comes back absent and an unset one comes back null; both mean "no label", and so
    /// does an empty or blank string, which would otherwise draw a link with nothing to click.
    /// </remarks>
    public static string? Label(object? value) => value switch
    {
        null => null,
        string text => string.IsNullOrWhiteSpace(text) ? null : text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>The distinct ids among <paramref name="values"/>, in lists the data port accepts.</summary>
    public static IReadOnlyList<IReadOnlyList<Guid>> Batches(IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return [.. values
            .Select(IdOf)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .Chunk(AlvoFilter.MaxInCandidates)];
    }

    /// <summary>The label of <paramref name="entity"/>'s rows, when it has one.</summary>
    public static RowLabel? For(EntitySchema entity, FieldMasks masks)
    {
        var strings = GridColumns.Readable(entity, masks).Where(field => field.Type == FieldType.String).ToList();
        var byName = strings.ToDictionary(field => field.Name, StringComparer.Ordinal);

        var named = NameLike.FirstOrDefault(byName.ContainsKey)
            ?? strings.FirstOrDefault(field => field.Name.EndsWith(NumberSuffix, StringComparison.Ordinal))?.Name;
        if (named is not null)
        {
            return Label([named], masks);
        }

        if (byName.ContainsKey("first_name") && byName.ContainsKey("last_name"))
        {
            return Label(["first_name", "last_name"], masks);
        }

        var first = strings.FirstOrDefault(field => field.Required) ?? strings.FirstOrDefault();
        return first is null ? null : Label([first.Name], masks);
    }

    /// <summary>A label over <paramref name="fields"/>, searchable by those no mask may withhold.</summary>
    private static RowLabel Label(IReadOnlyList<string> fields, FieldMasks masks)
        => new(fields) { Searchable = [.. fields.Where(field => !masks.MayBeHidden(field))] };

    /// <summary>The read that fetches one batch's labels from <paramref name="entity"/>.</summary>
    public static AlvoQuery Query(string entity, RowLabel label, IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(ids);

        return new AlvoQuery
        {
            Entity = entity,
            Filter = new AlvoComparison(AlvoManagedColumns.Id, AlvoFilterOperator.In, ids),
            Select = [AlvoManagedColumns.Id, .. label.Fields],
            Limit = ids.Count,
        };
    }
}

/// <summary>The fields whose values, joined by a space, name one row.</summary>
/// <param name="Fields">The label's fields, in the order they are read out.</param>
internal sealed record RowLabel(IReadOnlyList<string> Fields)
{
    /// <summary>The label's fields a search or a sort may name: all of them, less any a CEL mask may withhold.</summary>
    /// <remarks>
    /// A CEL-masked field can still be read out — a caller it hides from gets the short id — but it is never put
    /// in a filter or a sort, because the data port refuses the whole query for a caller the field is hidden
    /// from (the rule <see cref="GridQuery.Searchable"/> keeps for the grid; <see cref="FieldMasks"/> says why).
    /// </remarks>
    public IReadOnlyList<string> Searchable { get; init; } = Fields;

    /// <summary>Whether <paramref name="field"/> is part of the label.</summary>
    public bool Covers(string field) => Fields.Contains(field, StringComparer.Ordinal);

    /// <summary>The row's label; <see langword="null"/> when none of its fields has a value.</summary>
    /// <remarks>A field that is masked or unset is skipped rather than drawn as a gap.</remarks>
    public string? Of(AlvoRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var parts = Fields.Select(field => RefLabels.Label(row[field])).OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }
}
