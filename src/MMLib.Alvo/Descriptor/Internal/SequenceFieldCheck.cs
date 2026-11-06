using MMLib.Alvo.Schema;
using FieldType = MMLib.Alvo.Descriptor.FieldType;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The apply-time findings for a field's <c>sequence</c>: what it may sit on, what it may not be combined with, the
/// field its count runs <c>per</c>, the grammar of its <c>format</c>, and its refusal on a <c>storage: dynamic</c> entity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the parsed descriptor, before the mapper drops dynamic entities</b>, so the dynamic refusal has a
/// pointer — the mapper refuses the same declaration with an exception, which an embedded host that never validates
/// still meets.
/// </para>
/// <para>
/// A sequence is refused beside <c>default</c>, <c>computed</c>, <c>rollup</c> and <c>maxLength</c> because each of
/// those claims the value or bounds it, while the framework assigns it; and <c>per</c> must name a <c>required</c> field
/// whose value is a discrete key, because a missing or continuous value has no count to start.
/// </para>
/// </remarks>
internal static class SequenceFieldCheck
{
    private static readonly HashSet<FieldType> _countableTypes =
    [
        FieldType.String, FieldType.Integer, FieldType.Enum, FieldType.Ref, FieldType.Uuid, FieldType.Date, FieldType.Boolean,
    ];

    /// <summary>Every finding about every declared <c>sequence</c>.</summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static IReadOnlyList<DescriptorValidationError> Check(AlvoDescriptor descriptor) =>
    [
        .. descriptor.Entities.SelectMany(entity => entity.Value.Fields
            .Where(field => field.Value.Sequence is not null)
            .SelectMany(field => Errors(entity.Key, entity.Value, field.Key, field.Value))),
    ];

    /// <summary>The sentence that refuses a <c>sequence</c> on a <c>storage: dynamic</c> entity.</summary>
    /// <param name="entity">The dynamic entity.</param>
    /// <param name="field">The field declaring the sequence.</param>
    internal static string DynamicMessage(string entity, string field) =>
        $"Field '{entity}.{field}' declares 'sequence', which the framework counts in a table of its own, and '{entity}' "
        + "declares 'storage: dynamic' — dynamic entities have no table to hold one until #41 defines how their integrity is enforced.";

    private static IEnumerable<DescriptorValidationError> Errors(
        string entity, EntityDescriptor declaring, string field, FieldDescriptor descriptor)
    {
        var finding = new Finding(entity, field);
        if (!DescriptorToSchemaMapper.IsPhysical(declaring))
        {
            return [finding.At(string.Empty, DynamicMessage(entity, field), $"Remove 'sequence', or make '{entity}' a physical entity.")];
        }

        return new[] { TypeError(finding, descriptor), ConflictError(finding, descriptor), PerError(finding, declaring, descriptor) }
            .OfType<DescriptorValidationError>();
    }

    private static DescriptorValidationError? TypeError(Finding finding, FieldDescriptor descriptor)
    {
        var format = descriptor.Sequence!.Format;

        return descriptor.Type switch
        {
            FieldType.Integer when format is not null => finding.At(
                "/format",
                $"Field '{finding.Name}' declares a 'format' on an integer 'sequence', which stores the number itself.",
                "Remove 'format', or make the field a string."),
            FieldType.Integer => null,
            FieldType.String when format is null => finding.At(
                string.Empty,
                $"Field '{finding.Name}' declares a string 'sequence' without 'format', so there is no text to store the number in.",
                "Add a 'format' such as \"SO-{yyyy}-{n:4}\", or make the field an integer."),
            FieldType.String => FormatError(finding, format!),
            _ => finding.At(
                string.Empty,
                $"Field '{finding.Name}' declares 'sequence', which only an integer or a string field can hold.",
                "Make the field an integer, or a string with a 'format'."),
        };
    }

    private static DescriptorValidationError? FormatError(Finding finding, string format) =>
        SequenceSchema.IsValidFormat(format)
            ? null
            : finding.At(
                "/format",
                $"Field '{finding.Name}' declares the 'sequence' format '{format}', which needs exactly one {{n}} or {{n:width}} "
                + "and no braces other than {yyyy}, {yy}, {MM} and {dd}.",
                "Write one {n} (or {n:4} to pad to four digits) and only the date tokens {yyyy}, {yy}, {MM}, {dd}.");

    private static DescriptorValidationError? ConflictError(Finding finding, FieldDescriptor descriptor)
    {
        var keyword = descriptor switch
        {
            { Default: not null } => "default",
            { Computed: not null } => "computed",
            { Rollup: not null } => "rollup",
            { MaxLength: not null } => "maxLength",
            _ => null,
        };

        return keyword is null
            ? null
            : finding.At(
                string.Empty,
                $"Field '{finding.Name}' declares both 'sequence' and '{keyword}', but the framework assigns a sequence's value.",
                $"Remove '{keyword}', or remove 'sequence'.");
    }

    private static DescriptorValidationError? PerError(Finding finding, EntityDescriptor declaring, FieldDescriptor descriptor)
    {
        if (descriptor.Sequence!.Per is not { } per)
        {
            return null;
        }

        var reason = PerReason(finding, declaring, per);

        return reason is null
            ? null
            : finding.At(
                "/per",
                $"Field '{finding.Name}' counts its 'sequence' per '{per}', which {reason}.",
                "Name a required string, integer, enum, ref, uuid, date or boolean field of this entity in 'per', or remove it.");
    }

    private static string? PerReason(Finding finding, EntityDescriptor declaring, string per)
    {
        if (per == finding.Field)
        {
            return "names the sequence itself";
        }

        if (!declaring.Fields.TryGetValue(per, out var target))
        {
            return $"names no field of '{finding.Entity}'";
        }

        if (!_countableTypes.Contains(target.Type))
        {
            return $"is a {target.Type.ToString().ToLowerInvariant()} field and has no value to count by";
        }

        return target.Required == true ? null : "names a field that is not required";
    }

    private readonly record struct Finding(string Entity, string Field)
    {
        internal string Name => $"{Entity}.{Field}";

        internal DescriptorValidationError At(string member, string message, string fix) =>
            new($"/entities/{Entity}/fields/{Field}/sequence{member}", message, fix, DescriptorValidationSeverity.Error);
    }
}
