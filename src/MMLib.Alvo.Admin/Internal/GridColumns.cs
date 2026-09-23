using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Which of an entity's fields the Data grid shows, which one names a row, and what each header says.
/// </summary>
/// <remarks>
/// <para>
/// <b>The label is a heuristic, not a descriptor key</b> (design pass §4.1). The schema has no
/// <c>displayField</c>, and inventing one is a schema change outside this pass; the rule is
/// deterministic — the first required <c>string</c>, else the first <c>string</c> — so a later
/// <c>displayField</c> replaces it without the screens noticing.
/// </para>
/// <para>
/// <b>Columns by what a reader scans for, not by declaration order.</b> The first seven declared
/// fields hid the ones people open the screen for; the order here is the label, then enums (the
/// state of the row), references, dates, amounts, and the rest. <c>json</c> and <c>text</c> stay
/// out because one row of either fills the viewport; computed and rollup fields stay in because
/// they are the interesting numbers.
/// </para>
/// </remarks>
internal static class GridColumns
{
    /// <summary>How many columns the grid shows.</summary>
    public const int Cap = 7;

    private const string IdSuffix = "_id";

    /// <summary>The field that names a row of <paramref name="entity"/>, when it has one.</summary>
    public static FieldSchema? LabelField(EntitySchema entity, FieldMasks masks)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(masks);

        var strings = Readable(entity, masks).Where(field => field.Type == FieldType.String).ToList();
        return strings.FirstOrDefault(field => field.Required) ?? strings.FirstOrDefault();
    }

    /// <summary>The columns the grid shows, in the order it shows them.</summary>
    public static IReadOnlyList<FieldSchema> Choose(EntitySchema entity, FieldMasks masks)
    {
        var label = LabelField(entity, masks)?.Name;
        return [.. Readable(entity, masks)
            .Where(field => field.Type is not (FieldType.Json or FieldType.Text))
            .OrderBy(field => Rank(field, label))
            .Take(Cap)];
    }

    /// <summary>A column's header: <c>order_number</c> reads "Order number", <c>bike_id</c> reads "Bike".</summary>
    /// <remarks>
    /// The <c>_id</c> is dropped only from a reference, because that is the one column whose cell no
    /// longer shows an id — it shows the row the id points at.
    /// </remarks>
    public static string Header(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var name = field.Type == FieldType.Ref
            && field.Name.Length > IdSuffix.Length
            && field.Name.EndsWith(IdSuffix, StringComparison.Ordinal)
                ? field.Name[..^IdSuffix.Length]
                : field.Name;

        return Humanise(name);
    }

    /// <summary>An identifier as a sentence-case phrase.</summary>
    public static string Humanise(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var words = identifier.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return identifier;
        }

        var phrase = string.Join(' ', words).ToLowerInvariant();
        return string.Concat(phrase[..1].ToUpperInvariant(), phrase[1..]);
    }

    private static int Rank(FieldSchema field, string? label)
    {
        if (string.Equals(field.Name, label, StringComparison.Ordinal))
        {
            return 0;
        }

        return field.Type switch
        {
            FieldType.Enum => 1,
            FieldType.Ref => 2,
            FieldType.Date or FieldType.DateTime => 3,
            FieldType.Decimal => 4,
            _ => 5,
        };
    }

    /// <summary>
    /// The fields a caller can be shown at all: not the managed columns, and not one declared hidden
    /// from everyone.
    /// </summary>
    private static IEnumerable<FieldSchema> Readable(EntitySchema entity, FieldMasks masks)
    {
        var managed = AlvoManagedColumns.For(entity);
        return entity.Fields.Where(field => !managed.Contains(field.Name) && !masks.NeverReturned(field.Name));
    }
}
