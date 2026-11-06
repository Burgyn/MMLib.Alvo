using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// What the Indexes tab's add form holds — the fields, <i>Unique</i>, <i>Only where</i> and <i>Exclude overlaps</i> — and the
/// index it builds from them.
/// </summary>
/// <remarks>
/// <para>
/// Out of <c>Indexes.razor</c> so it can be tested (the F-13 rule). It refuses only what the form can say at the control:
/// no field, an exclusion missing an end, a filter over the schema's length. Everything else — a filter over a field the
/// entity lacks, a hidden range field — is the apply's, and the <i>Only where</i> box asks it live.
/// </para>
/// <para>
/// <b>The range is offered from the fields <c>ExclusionIndexCheck</c> accepts</b>: required <c>date</c> or
/// <c>datetime</c> fields a caller writes; the end from the start's own type.
/// </para>
/// </remarks>
internal sealed class IndexDraft
{
    private const int CelMaxLength = 2000;

    /// <summary>The fields picked, in the order they were picked.</summary>
    public List<string> Chosen { get; set; } = [];

    /// <summary>Whether the index enforces uniqueness — never beside an exclusion.</summary>
    public bool Unique { get; set; }

    /// <summary>The <c>where</c> as typed, or empty for every row.</summary>
    public string Where { get; set; } = string.Empty;

    /// <summary>Whether the index excludes overlapping ranges.</summary>
    public bool Excludes { get; set; }

    /// <summary>The field the range starts at.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>The field the range ends at.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Whether <i>Unique</i> is drawn: an exclusion already refuses equal fields whose ranges overlap.</summary>
    public bool UniqueOffered => !Excludes;

    /// <summary>Whether the form differs from an empty one.</summary>
    public bool Dirty => Chosen.Count > 0 || Unique || Excludes || Where.Length > 0;

    /// <summary>The fields a range may start at: required <c>date</c> and <c>datetime</c> fields a caller writes.</summary>
    /// <param name="entity">The entity, as the tab draws it.</param>
    public static IReadOnlyList<string> RangeFields(EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var managed = AlvoManagedColumns.For(entity);
        return [.. entity.Fields.Where(field => IsRangeEnd(field) && !managed.Contains(field.Name)).Select(field => field.Name)];
    }

    /// <summary>The fields the range may end at: <see cref="RangeFields"/> of the start's type, the start excepted.</summary>
    /// <param name="entity">The entity, as the tab draws it.</param>
    public IReadOnlyList<string> EndFields(EntitySchema entity)
    {
        var start = entity.Fields.FirstOrDefault(field => field.Name == From);
        return [.. RangeFields(entity).Where(name => name != From
            && (start is null || entity.Fields.First(field => field.Name == name).Type == start.Type))];
    }

    /// <summary>The index as it stands, refused or not — what the live check of <i>Only where</i> places.</summary>
    public IndexSchema Candidate() => new([.. Chosen], Unique && UniqueOffered)
    {
        Where = Where.Trim() is { Length: > 0 } where ? where : null,
        Exclude = Excludes ? new ExclusionSchema(From, To) : null,
    };

    /// <summary>The index to declare, or <see langword="null"/> with <paramref name="refusal"/> saying why it cannot be.</summary>
    /// <param name="refusal">Why, when it cannot.</param>
    public IndexSchema? Build(out string? refusal)
    {
        refusal = Refusal();
        return refusal is null ? Candidate() : null;
    }

    private string? Refusal()
    {
        if (Chosen.Count == 0)
        {
            return "An index covers at least one field. Pick one above.";
        }

        if (Excludes && (From.Length == 0 || To.Length == 0))
        {
            return "An exclusion needs the field its range starts at and the one it ends at — pick both ends.";
        }

        return Where.Trim().Length > CelMaxLength
            ? string.Create(CultureInfo.InvariantCulture, $"A filter is at most {CelMaxLength} characters — the schema's limit for CEL.")
            : null;
    }

    private static bool IsRangeEnd(FieldSchema field)
        => field.Required && field.Type is FieldType.Date or FieldType.DateTime && !field.IsMaintained();
}
