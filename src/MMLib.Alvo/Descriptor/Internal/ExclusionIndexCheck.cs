using DescFieldType = MMLib.Alvo.Descriptor.FieldType;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The apply-time findings for an index's <c>exclude</c>: its range is two required <c>date</c> or <c>datetime</c> fields of
/// one type that the caller writes and every reader may see, neither of them an equality field, beside equality fields an
/// engine can compare, on an index that is not also <c>unique</c> (HX-12 D1, D17, D18).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the parsed descriptor, before the mapper,</b> as <see cref="SequenceFieldCheck"/> is: the mapper synthesizes
/// the range's <c>to &gt;= from</c> validation only over a well-formed pair, so a malformed one is reported here once, at the
/// author's own pointer. A <c>storage: dynamic</c> entity is <see cref="IndexFilterCheck.DynamicErrors"/>' refusal; its
/// <c>where</c> is <see cref="IndexFilterCheck"/>'s.
/// </para>
/// <para>
/// <b>A hidden range field is refused, a hidden equality field is not.</b> A 409 naming the range, or an apply's count of
/// overlapping rows, tells a caller where a range they cannot read lies; an equality field's refusal is the existence oracle
/// a <c>unique</c> index on a hidden field already is.
/// </para>
/// </remarks>
internal static class ExclusionIndexCheck
{
    /// <summary>Every finding about every declared <c>exclude</c> of a physical entity.</summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static IReadOnlyList<DescriptorValidationError> Check(AlvoDescriptor descriptor) =>
    [
        .. from entity in descriptor.Entities
           where DescriptorToSchemaMapper.IsPhysical(entity.Value)
           let indexes = entity.Value.Indexes ?? []
           from position in Enumerable.Range(0, indexes.Count)
           where indexes[position].Exclude is not null
           from error in Errors(new Declared(entity.Key, entity.Value, position, indexes[position]))
           select error,
    ];

    private static IEnumerable<DescriptorValidationError> Errors(Declared declared)
    {
        var range = declared.Index.Exclude!;
        IEnumerable<DescriptorValidationError?> findings =
        [
            EndError(declared, range.From, "/from"),
            EndError(declared, range.To, "/to"),
            PairError(declared, range),
            UniqueError(declared),
            .. declared.Index.Fields.Select(field => EqualityError(declared, field)),
        ];

        return findings.OfType<DescriptorValidationError>();
    }

    private static DescriptorValidationError? EndError(Declared declared, string name, string end)
    {
        if (!declared.Entity.Fields.TryGetValue(name, out var field))
        {
            return declared.At(end, $"excludes overlapping ranges over '{name}', which '{declared.Name}' does not declare.", $"Declare '{name}' as a required date or datetime field.");
        }

        return MaintainedError(declared, name, end, field)
            ?? TypeError(declared, name, end, field)
            ?? RoleError(declared, name, end)
            ?? (field.Required == true ? null : declared.At(end, $"excludes overlapping ranges over '{name}', which is not required — a range without an end has no extent to compare.", $"Mark '{name}' required."))
            ?? HiddenError(declared, name, end, field);
    }

    private static DescriptorValidationError? MaintainedError(Declared declared, string name, string end, FieldDescriptor field) =>
        field.Computed is not null || field.Rollup is not null || field.Sequence is not null
            ? declared.At(end, $"excludes overlapping ranges over '{name}', which is maintained by the framework rather than written.", "Bound the range with fields the caller writes.")
            : null;

    private static DescriptorValidationError? TypeError(Declared declared, string name, string end, FieldDescriptor field) =>
        field.Type is DescFieldType.Date or DescFieldType.DateTime
            ? null
            : declared.At(end, $"excludes overlapping ranges over '{name}', which is not a date or a datetime.", "Bound the range with two date or two datetime fields.");

    private static DescriptorValidationError? RoleError(Declared declared, string name, string end) =>
        declared.Index.Fields.Contains(name, StringComparer.Ordinal)
            ? declared.At(end, $"excludes overlapping ranges over '{name}', which is also one of the index's 'fields' — a range bound is compared by overlap, never by equality.", $"Remove '{name}' from 'fields'.")
            : null;

    private static DescriptorValidationError? HiddenError(Declared declared, string name, string end, FieldDescriptor field) =>
        field.Hidden is { IsExpression: true } or { Boolean: true }
            ? declared.At(
                end,
                $"excludes overlapping ranges {(end == "/from" ? "starting" : "ending")} at '{name}', which is hidden: a refused write would disclose where a range its readers cannot see lies.",
                $"Unhide '{name}', or enforce the overlap in a before-hook whose refusal names no stored value.")
            : null;

    private static DescriptorValidationError? PairError(Declared declared, IndexExclusion range)
    {
        if (!declared.Entity.Fields.TryGetValue(range.From, out var from) || !declared.Entity.Fields.TryGetValue(range.To, out var to)
            || from.Type == to.Type || from.Type is not (DescFieldType.Date or DescFieldType.DateTime) || to.Type is not (DescFieldType.Date or DescFieldType.DateTime))
        {
            return null;
        }

        return declared.At(
            "/to",
            $"excludes overlapping ranges from '{range.From}' ({Spelled(from.Type)}) to '{range.To}' ({Spelled(to.Type)}) — both ends must be of one type.",
            $"Make '{range.From}' and '{range.To}' both dates or both datetimes.");
    }

    private static DescriptorValidationError? UniqueError(Declared declared) =>
        declared.Index.Unique == true
            ? declared.At(string.Empty, "is both 'unique' and an exclusion — an exclusion already refuses equal fields whose ranges overlap.", "Remove 'unique', or declare the unique index separately.")
            : null;

    private static DescriptorValidationError? EqualityError(Declared declared, string name) =>
        declared.Entity.Fields.TryGetValue(name, out var field) && field.Type == DescFieldType.Json
            ? declared.At(string.Empty, $"excludes overlapping ranges per '{name}', a json field, which no engine compares for equality.", $"Remove '{name}' from 'fields'.")
            : null;

    private static string Spelled(DescFieldType type) => type == DescFieldType.Date ? "date" : "datetime";

    private sealed record Declared(string Name, EntityDescriptor Entity, int Position, EntityIndex Index)
    {
        internal DescriptorValidationError At(string suffix, string reason, string fix) => new(
            $"{IndexFilterCheck.Pointer(Name, Position)}/exclude{suffix}",
            $"An index of '{Name}' {reason}",
            fix,
            DescriptorValidationSeverity.Error);
    }
}
