using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Which of an entity's fields the Data grid shows, in what order, and what each header says.
/// </summary>
/// <remarks>
/// <para>
/// <b>Columns by what a reader scans for, not by declaration order.</b> The first seven declared
/// fields hid the ones people open the screen for; the order here is the label (see
/// <see cref="RefLabels.For"/>), up to <see cref="LeadingEnums"/> enums (the state of the row),
/// references, amounts, dates, the remaining enums, and the rest. <c>json</c> and <c>text</c> stay
/// out because one row of either fills the viewport; computed and rollup fields stay in, with the
/// amounts, because they are the interesting numbers.
/// </para>
/// </remarks>
internal static class GridColumns
{
    /// <summary>How many columns the grid shows.</summary>
    public const int Cap = 7;

    /// <summary>How many enums come straight after the label; the rest wait until after the dates.</summary>
    public const int LeadingEnums = 2;

    private const string IdSuffix = "_id";

    /// <summary>The columns the grid shows, in the order it shows them.</summary>
    /// <remarks>
    /// Enums are split: the first <see cref="LeadingEnums"/> sit right after the label, because a row's
    /// state is what a reader scans first, and the rest go after the dates — four status-like columns
    /// otherwise filled the cap before a single amount or date was shown.
    /// </remarks>
    public static IReadOnlyList<FieldSchema> Choose(EntitySchema entity, FieldMasks masks)
    {
        var label = RefLabels.For(entity, masks);
        var eligible = Readable(entity, masks)
            .Where(field => field.Type is not (FieldType.Json or FieldType.Text))
            .ToList();
        var leading = eligible.Where(field => field.Type == FieldType.Enum && label?.Covers(field.Name) != true)
            .Take(LeadingEnums)
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

        return [.. eligible.OrderBy(field => Rank(field, label, leading)).Take(Cap)];
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

    private static int Rank(FieldSchema field, RowLabel? label, HashSet<string> leadingEnums)
    {
        if (label?.Covers(field.Name) == true)
        {
            return 0;
        }

        return field.Type switch
        {
            FieldType.Enum when leadingEnums.Contains(field.Name) => 1,
            FieldType.Ref => 2,
            FieldType.Decimal => 3,
            FieldType.Date or FieldType.DateTime => 4,
            FieldType.Enum => 5,
            _ => 6,
        };
    }

    /// <summary>
    /// The fields a caller can be shown at all: not the managed columns, and not one declared hidden
    /// from everyone.
    /// </summary>
    public static IEnumerable<FieldSchema> Readable(EntitySchema entity, FieldMasks masks)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(masks);

        var managed = AlvoManagedColumns.For(entity);
        return entity.Fields.Where(field => !managed.Contains(field.Name) && !masks.NeverReturned(field.Name));
    }
}
