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

    /// <summary>
    /// Gets a value indicating whether the field is required (descriptor-facing intent). This does
    /// not drive the physical column directly — <see cref="Nullable"/> does; the descriptor mapper
    /// reconciles the two (a field is nullable unless <c>required</c> or <c>nullable:false</c> is
    /// set). When building a <see cref="FieldSchema"/> by hand, set <see cref="Nullable"/> to control
    /// column nullability.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>Gets a value indicating whether the field must be unique.</summary>
    public bool Unique { get; init; }

    /// <summary>
    /// Gets a value indicating whether the field is nullable. This is the authoritative driver of
    /// physical column nullability (NULL vs NOT NULL); <see cref="Required"/> is descriptor intent
    /// the mapper folds into this.
    /// </summary>
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

    /// <summary>
    /// Gets the validation format this field's values must satisfy — a built-in name
    /// (<c>email</c>/<c>uri</c>/<c>phone</c>) or the name of a format the descriptor's top-level
    /// <c>formats</c> block declares; <see langword="null"/> when the field declares none.
    /// </summary>
    /// <remarks>
    /// On the applied schema rather than left in the descriptor because the two layers that need it cannot
    /// see the descriptor: the HTTP layer enforces the format (the schema's own words — "enforced by Alvo
    /// at the API layer") and the generated OpenAPI document reflects it. It names the <em>format</em>, not
    /// its pattern, so a document can emit <c>format: email</c> for a built-in rather than leaking a regex
    /// nobody authored.
    /// </remarks>
    public string? Format { get; init; }

    /// <summary>
    /// Gets the regular expression <see cref="Format"/> resolves to for a descriptor-declared format;
    /// <see langword="null"/> for a built-in format (whose pattern the framework owns) and for a field
    /// with no format at all.
    /// </summary>
    /// <remarks>
    /// <b>Resolved onto the field rather than kept in a schema-level map, deliberately.</b> A
    /// <see cref="SchemaModel"/> is rebuilt from its entities on more than one path — a rename pre-pass
    /// aligns the current model, an introspector composes one from the database — and each of those
    /// reconstructs the model from <see cref="EntitySchema"/> instances. A map beside
    /// <see cref="SchemaModel.Entities"/> would be silently dropped by every one of them, and a dropped
    /// pattern is a field that quietly stops being validated. Carried on the field, it survives every
    /// rebuild that preserves the field.
    /// </remarks>
    public string? FormatPattern { get; init; }

    /// <summary>Gets a value indicating whether the field is indexed.</summary>
    public bool Indexed { get; init; }

    /// <summary>Gets the computed expression for computed fields.</summary>
    public string? ComputedExpression { get; init; }
}
