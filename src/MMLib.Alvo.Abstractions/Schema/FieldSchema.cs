namespace MMLib.Alvo.Schema;

/// <summary>Describes a field in an entity schema.</summary>
public sealed record FieldSchema
{
    /// <summary>Gets the field name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the field type.</summary>
    public required FieldType Type { get; init; }

    /// <summary>Gets the previous name of the field (for migrations).</summary>
    public string? RenamedFrom { get; init; }

    /// <summary>Gets a value indicating whether the field is required.</summary>
    public bool Required { get; init; }

    /// <summary>Gets a value indicating whether the field must be unique.</summary>
    public bool Unique { get; init; }

    /// <summary>Gets a value indicating whether the field is nullable.</summary>
    public bool Nullable { get; init; }

    /// <summary>Gets the maximum length for string fields.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Gets the precision for decimal fields.</summary>
    public int? Precision { get; init; }

    /// <summary>Gets the scale for decimal fields.</summary>
    public int? Scale { get; init; }

    /// <summary>Gets the enum values for enum-type fields.</summary>
    public IReadOnlyList<string>? EnumValues { get; init; }

    /// <summary>Gets the reference information for reference-type fields.</summary>
    public RefSchema? Reference { get; init; }

    /// <summary>Gets a value indicating whether the field is indexed.</summary>
    public bool Indexed { get; init; }

    /// <summary>Gets the computed expression for computed fields.</summary>
    public string? ComputedExpression { get; init; }
}
