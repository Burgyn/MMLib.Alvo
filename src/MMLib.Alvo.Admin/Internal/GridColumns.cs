using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Which of an entity's fields the Data grid shows, in what order, and what each header says.
/// </summary>
/// <remarks>
/// <para>
/// <b>Columns by what a reader scans for, not by declaration order.</b> The first seven declared
/// fields hid the ones people open the screen for. <c>json</c> and <c>text</c> stay out because one row
/// of either fills the viewport; computed and rollup fields stay in, because they are the interesting
/// numbers. Up to <see cref="Cap"/> columns, in this order:
/// </para>
/// <list type="number">
/// <item>the label (see <see cref="RefLabels.For"/>);</item>
/// <item>up to <see cref="LeadingEnums"/> enums — the state of the row;</item>
/// <item>up to <see cref="LeadingRefs"/> references;</item>
/// <item>one amount: a decimal named <c>total</c>, else <c>amount</c>, else <c>grand_total</c>, else the
/// last declared name ending in <c>_total</c>, else the last declared computed or rollup decimal, else the
/// first decimal;</item>
/// <item>one date: the first declared date or instant ending in <c>_on</c>, else in <c>_at</c>, else in
/// <c>_date</c>, else named <c>due</c> or <c>deadline</c>, else the first date;</item>
/// <item>then whatever is left, as references, amounts, dates, enums, and the rest.</item>
/// </list>
/// <para>
/// <b>The date's suffixes are tried in turn, not together.</b> A ruling listed them as one set with the
/// first declared winning, which picks <c>received_at</c> over <c>promised_on</c> on a service order — the
/// date nobody opens the screen for. <c>_on</c> names a day something is due or done; <c>_at</c> is as
/// often a bookkeeping instant.
/// </para>
/// <para>
/// <b>A heuristic, and knowingly so.</b> Every rule above is a guess about names. The proper follow-up is
/// a column picker in the grid, or a descriptor <c>displayField</c>/column list — a schema change outside
/// this pass — which would replace this without the screen noticing.
/// </para>
/// </remarks>
internal static class GridColumns
{
    /// <summary>How many columns the grid shows.</summary>
    public const int Cap = 8;

    /// <summary>How many enums come straight after the label.</summary>
    public const int LeadingEnums = 2;

    /// <summary>How many references come straight after those enums.</summary>
    public const int LeadingRefs = 2;

    private static readonly string[] _amountNames = ["total", "amount", "grand_total"];

    private static readonly string[] _dateSuffixes = ["_on", "_at", "_date"];

    private static readonly string[] _dateNames = ["due", "deadline"];

    private const string IdSuffix = "_id";

    /// <summary>The columns the grid shows, in the order it shows them.</summary>
    public static IReadOnlyList<FieldSchema> Choose(EntitySchema entity, FieldMasks masks)
    {
        var label = RefLabels.For(entity, masks);
        var eligible = Readable(entity, masks)
            .Where(field => field.Type is not (FieldType.Json or FieldType.Text))
            .ToList();
        var rest = eligible.Where(field => label?.Covers(field.Name) != true).ToList();

        List<FieldSchema?> leading =
        [
            .. eligible.Where(field => label?.Covers(field.Name) == true),
            .. rest.Where(field => field.Type == FieldType.Enum).Take(LeadingEnums),
            .. rest.Where(field => field.Type == FieldType.Ref).Take(LeadingRefs),
            Amount([.. rest.Where(field => field.Type == FieldType.Decimal)]),
            Date([.. rest.Where(field => field.Type is FieldType.Date or FieldType.DateTime)]),
        ];
        var chosen = leading.OfType<FieldSchema>().ToList();

        return [.. chosen.Concat(rest.Except(chosen).OrderBy(Rank)).Take(Cap)];
    }

    /// <summary>The one amount shown up front, when the entity has a decimal at all.</summary>
    private static FieldSchema? Amount(List<FieldSchema> decimals)
        => _amountNames
                .Select(name => decimals.FirstOrDefault(field => field.Name == name))
                .FirstOrDefault(field => field is not null)
            ?? decimals.LastOrDefault(field => field.Name.EndsWith("_total", StringComparison.Ordinal))
            ?? decimals.LastOrDefault(field => field.ComputedExpression is not null || field.Rollup is not null)
            ?? decimals.FirstOrDefault();

    /// <summary>The one date shown up front, when the entity has one.</summary>
    private static FieldSchema? Date(List<FieldSchema> dates)
        => _dateSuffixes
                .Select(suffix => dates.FirstOrDefault(field => field.Name.EndsWith(suffix, StringComparison.Ordinal)))
                .FirstOrDefault(field => field is not null)
            ?? dates.FirstOrDefault(field => _dateNames.Contains(field.Name, StringComparer.Ordinal))
            ?? dates.FirstOrDefault();

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

    /// <summary>Where a field not placed up front falls among the rest.</summary>
    private static int Rank(FieldSchema field) => field.Type switch
    {
        FieldType.Ref => 0,
        FieldType.Decimal => 1,
        FieldType.Date or FieldType.DateTime => 2,
        FieldType.Enum => 3,
        _ => 4,
    };

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
