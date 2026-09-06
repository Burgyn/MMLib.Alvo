namespace MMLib.Alvo.Schema;

/// <summary>Describes a field in an entity schema.</summary>
public sealed record FieldSchema
{
    /// <summary>Gets the field name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the field type.</summary>
    public required FieldType Type { get; init; }

    /// <summary>
    /// Gets the human-readable description of this field, or <see langword="null"/> when the descriptor
    /// declares none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the applied schema rather than left in the descriptor for the reason <see cref="Format"/> is: the
    /// layer that needs it cannot see the descriptor. The generated OpenAPI document <em>is</em> the contract
    /// an agent reads (§0 principle 4), and a field described in the descriptor but undescribed in the
    /// published document loses the one sentence saying what the field means — which no type, length or
    /// format replaces.
    /// </para>
    /// <para>
    /// It carries no behaviour: nothing validates against it, no column stores it, and the migration diff
    /// (<c>SchemaDiff.IsUnchanged</c>) deliberately does not consult it — so rewording a description is not a
    /// schema change and plans no migration.
    /// </para>
    /// </remarks>
    public string? Description { get; init; }

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

    /// <summary>
    /// Gets the maximum length for string fields, in <b>Unicode code points</b>.
    /// </summary>
    /// <remarks>
    /// Code points rather than UTF-16 code units, because that is what PostgreSQL's <c>varchar(n)</c>
    /// bounds and what JSON Schema's <c>maxLength</c> keyword means — so the validator, the column and the
    /// published document agree by construction on both shipped drivers. See <c>RecordValidator.TooLong</c>
    /// for why grapheme clusters are the wrong unit here even though they are the human one, and for the
    /// engine whose column unit differs (#175).
    /// </remarks>
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

    /// <summary>
    /// Gets the <b>CEL source</b> of this field's <c>computed</c> expression, or <see langword="null"/> when
    /// the field is not computed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>CEL, never the rendered SQL.</b> A <see cref="SchemaModel"/> is engine-agnostic and is persisted as
    /// the applied schema, so it must not carry one provider's spelling: the same stored schema is read by
    /// whichever driver is registered, and a SQLite-rendered expression restored under PostgreSQL would be
    /// DDL for the wrong engine. The translation happens per driver, at the point the migration model is
    /// built.
    /// </para>
    /// <para>
    /// A field carrying one becomes a <b>stored generated column</b>: the database computes and maintains the
    /// value, and refuses any write to it, so no hook, custom endpoint or bug can set it.
    /// </para>
    /// </remarks>
    public string? ComputedExpression { get; init; }

    /// <summary>
    /// Gets the aggregate over a child entity's records this field holds, or <see langword="null"/> when the
    /// field is not a rollup.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="ComputedExpression"/>, and refused at apply when both are declared:
    /// the two disagree about who owns the value — the engine maintains a generated column, the framework
    /// maintains a rollup — so whichever won, the other declaration would be a lie about a stored number.
    /// </remarks>
    public RollupSchema? Rollup { get; init; }
}
