using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// A field definition (schema <c>$defs/field</c>). Which members are meaningful
/// depends on <see cref="Type"/> (e.g. <see cref="Values"/> for <c>enum</c>,
/// <see cref="Entity"/>/<see cref="OnDelete"/> for <c>ref</c>).
/// </summary>
public sealed record FieldDescriptor
{
    /// <summary>The field's data type.</summary>
    public required FieldType Type { get; init; }

    /// <summary>Human-readable description of the field.</summary>
    public string? Description { get; init; }

    /// <summary>Previous name of this field, declaring a rename so apply preserves data.</summary>
    public string? RenamedFrom { get; init; }

    /// <summary>Whether the field must be present (NOT NULL).</summary>
    public bool? Required { get; init; }

    /// <summary>Whether values must be unique across the entity.</summary>
    public bool? Unique { get; init; }

    /// <summary>Explicitly allow NULL; otherwise derived from <see cref="Required"/>.</summary>
    public bool? Nullable { get; init; }

    /// <summary>Default value: a JSON literal or a tagged CEL expression evaluated at insert time.</summary>
    public ValueOrExpr? Default { get; init; }

    /// <summary>Maximum length; applies to <see cref="FieldType.String"/> only.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Total digits; required for and applies to <see cref="FieldType.Decimal"/> only.</summary>
    public int? Precision { get; init; }

    /// <summary>Fractional digits; required for and applies to <see cref="FieldType.Decimal"/> only.</summary>
    public int? Scale { get; init; }

    /// <summary>Allowed values; required for and applies to <see cref="FieldType.Enum"/> only.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Target entity of the reference; required for and applies to <see cref="FieldType.Ref"/> only.</summary>
    public string? Entity { get; init; }

    /// <summary>On-delete behaviour for the reference; applies to <see cref="FieldType.Ref"/> only.</summary>
    public OnDeleteAction? OnDelete { get; init; }

    /// <summary>Validation format: a built-in (email, uri, phone) or a named format; applies to <see cref="FieldType.String"/> only.</summary>
    public string? Format { get; init; }

    /// <summary>Optional CEL value validation (context: <c>value</c>, <c>new</c>).</summary>
    public string? Validation { get; init; }

    /// <summary>Create an index over the field.</summary>
    public bool? Index { get; init; }

    /// <summary>Whether the field is hidden from API responses (static or a per-role CEL expression).</summary>
    public BoolOrCel? Hidden { get; init; }

    /// <summary>Whether the field is read-only via the API (static or a per-role CEL expression).</summary>
    public BoolOrCel? ReadOnly { get; init; }

    /// <summary>CEL expression deriving the value from other fields of the same row; makes the field read-only.</summary>
    public string? Computed { get; init; }

    /// <summary>Value aggregated over related records; the framework maintains it and the field is read-only.</summary>
    public Rollup? Rollup { get; init; }

    /// <summary>Extension keys (<c>x-*</c>) preserved verbatim through apply and export.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>A rollup: a value aggregated over related child records (schema <c>field.rollup</c>).</summary>
public sealed record Rollup
{
    /// <summary>Child entity that references this one.</summary>
    public required string From { get; init; }

    /// <summary>Aggregate operation over the child records.</summary>
    public required RollupOp Op { get; init; }

    /// <summary>Field on the child entity to aggregate (required for all ops except count).</summary>
    public string? Field { get; init; }

    /// <summary>The FK field on the child pointing back to this parent; required when the child has multiple refs to this parent.</summary>
    public string? Via { get; init; }

    /// <summary>Optional CEL filter on the child records.</summary>
    public string? Where { get; init; }
}
