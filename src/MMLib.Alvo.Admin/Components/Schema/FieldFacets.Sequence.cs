using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The third maintained kind (HX-12): a number the framework assigns on create. What the editor offers is exactly what
   SequenceFieldCheck accepts — an integer, or a string with a format; a per field that is required, countable and
   written by callers; a start of 0 or more — and an existing declaration is written back as declared. */
internal sealed partial class FieldFacets
{
    private const string FormatMissing =
        "A string sequence needs a format — the text stored, with one {n} for the number, such as B-{yyyy}-{n:3}.";

    private const string FormatMalformed =
        "A format holds exactly one {n} or {n:width} (width 1 to 18), and no braces other than {yyyy}, {yy}, {MM} and {dd}.";

    private const string StartRefusal =
        "A sequence's start is a whole number of 0 or more — the schema's minimum. Leave it empty to start at 1.";

    private const string TypeRefusal =
        "A sequence is an integer or a string with a format — the number itself, or text around it.";

    /// <summary>The types <c>per</c> may name: a value a count can be keyed on (<c>SequenceFieldCheck</c>).</summary>
    private static readonly HashSet<FieldType> _countable =
        [FieldType.String, FieldType.Integer, FieldType.Enum, FieldType.Ref, FieldType.Uuid, FieldType.Date, FieldType.Boolean];

    /// <summary>The types a sequence is offered as.</summary>
    public static IReadOnlyList<FieldType> SequenceTypes { get; } = [FieldType.Integer, FieldType.String];

    /// <summary>The frozen schema's <c>sequence.period</c> values, after the empty one for none.</summary>
    public static IReadOnlyList<string> SequencePeriods { get; } = [string.Empty, "year", "month", "day"];

    /// <summary>The field whose value starts its own count, or empty for one count per tenant.</summary>
    public string SequencePer { get; set; } = string.Empty;

    /// <summary>The calendar period after which the count restarts — one of <see cref="SequencePeriods"/>.</summary>
    public string SequencePeriod { get; set; } = string.Empty;

    /// <summary>The first number as typed, or empty for the schema's default of 1.</summary>
    public string SequenceStart { get; set; } = string.Empty;

    /// <summary>A string sequence's format as typed.</summary>
    public string SequenceFormat { get; set; } = string.Empty;

    /// <summary>The fields <c>per</c> is offered over, from <see cref="SequencePerOffered"/>.</summary>
    public IReadOnlyList<string> PerOffered { get; set; } = [];

    /// <summary>The <c>per</c> chips: none, every offered field, and the declared one wherever the offer lacks it.</summary>
    public IReadOnlyList<string> PerChoices
        => DeclaredPer is { Length: > 0 } declared && !PerOffered.Contains(declared, StringComparer.Ordinal)
            ? [string.Empty, .. PerOffered, declared]
            : [string.Empty, .. PerOffered];

    /// <summary>What is wrong with the typed format, said at its box while it is typed; nothing for an integer.</summary>
    public string? SequenceFormatProblem => Type != FieldType.String ? null
        : string.IsNullOrWhiteSpace(SequenceFormat) ? FormatMissing
        : SequenceSchema.IsValidFormat(SequenceFormat) ? null
        : FormatMalformed;

    private string DeclaredPer => Text((_declared["sequence"] as JsonObject)?["per"]);

    /// <summary>The fields a sequence of <paramref name="self"/> may count per: required, countable, stored, and written by callers.</summary>
    /// <param name="fields">The entity's fields as the working copy carries them.</param>
    /// <param name="self">The sequence's own field, which is never offered.</param>
    public static IReadOnlyList<string> SequencePerOffered(IReadOnlyList<KeyValuePair<string, string>> fields, string? self)
        => [.. (fields ?? []).Where(field => field.Key != self && Countable(Parse(field.Value))).Select(field => field.Key)];

    /// <summary>The first number this sequence would assign, as stored, on <paramref name="now"/>'s UTC date — rendered by the drivers' own <c>SequenceFormat</c>; nothing while the form cannot say.</summary>
    /// <param name="now">The clock's instant.</param>
    public string? SequencePreview(DateTimeOffset now)
    {
        if (!SequenceTypes.Contains(Type) || !TryReadStart(out var start) || SequenceFormatProblem is not null)
        {
            return null;
        }

        var number = start ?? 1;
        return Type == FieldType.Integer
            ? number.ToString(CultureInfo.InvariantCulture)
            : MMLib.Alvo.Schema.SequenceFormat.Render(SequenceFormat, number, now);
    }

    private static bool Countable(JsonObject field)
        => Flag(field["required"])
            && Enum.TryParse<FieldType>(Text(field["type"]), ignoreCase: true, out var type) && _countable.Contains(type)
            && !_maintainedFacets.Any(field.ContainsKey) && !Flag(field["virtual"]);

    /// <summary>Reads a declared <c>sequence</c> into the boxes.</summary>
    private void ReadSequence(JsonObject sequence)
    {
        Kind = FieldKind.Sequence;
        SequencePer = Text(sequence["per"]);
        SequencePeriod = Text(sequence["period"]);
        SequenceStart = sequence["start"] is JsonValue start && start.TryGetValue<long>(out var first)
            ? first.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        SequenceFormat = Text(sequence["format"]);
    }

    /// <summary>Writes a sequence, in place where the declaration already has one.</summary>
    private string? WriteSequence(JsonObject facets)
    {
        if (RefuseSequence() is { } refusal)
        {
            return refusal;
        }

        facets.Remove("rollup");
        facets.Remove("computed");
        facets["type"] = Word(Type);
        ClearFacetsOfOtherTypes(facets);
        facets.Remove("maxLength");
        facets.Remove("format");
        WriteSequenceObject(SequenceObject(facets));
        Toggle(facets, "index", Indexed);
        return WriteDefault(facets);
    }

    private string? RefuseSequence()
        => !SequenceTypes.Contains(Type) ? TypeRefusal
            : SequenceFormatProblem ?? (TryReadStart(out _) ? null : StartRefusal);

    private void WriteSequenceObject(JsonObject sequence)
    {
        TryReadStart(out var start);
        SetOrRemove(sequence, "per", SequencePer);
        SetOrRemove(sequence, "period", SequencePeriod);
        if (start is { } first)
        {
            sequence["start"] = first;
        }
        else
        {
            sequence.Remove("start");
        }

        SetOrRemove(sequence, "format", Type == FieldType.String ? SequenceFormat : null);
    }

    private static JsonObject SequenceObject(JsonObject facets)
    {
        if (facets["sequence"] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        facets["sequence"] = created;
        return created;
    }

    /// <summary>The typed start, or <see langword="null"/> for none; false when the box holds what the schema refuses.</summary>
    private bool TryReadStart(out long? start)
    {
        start = null;
        if (string.IsNullOrWhiteSpace(SequenceStart))
        {
            return true;
        }

        if (long.TryParse(SequenceStart.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var first))
        {
            start = first;
            return true;
        }

        return false;
    }
}
