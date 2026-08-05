namespace MMLib.Alvo.Schema;

/// <summary>
/// Describes a <b>rollup</b> field on the applied schema: a value aggregated over the records of a child
/// entity that references this one, which the framework maintains inside the child write's own transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Via"/> is required here while the descriptor's <c>via</c> is optional, and that is the whole
/// reason this type exists rather than the descriptor's own <c>Rollup</c> being reused.</b> The descriptor
/// lets an author omit it when the child has exactly one reference back to the parent; the mapper resolves it
/// there, once, against the child's declared <c>ref</c> fields, and refuses an ambiguous or absent one. Every
/// layer below therefore reads a foreign key rather than re-deriving one — and re-deriving it is what would
/// let the write path and the apply-time check disagree about which relationship a rollup follows, which is a
/// stored number aggregated over the wrong set of rows.
/// </para>
/// <para>
/// It carries no <c>where</c>. The frozen schema declares one and this build refuses it
/// (<c>UnhonouredFeatures</c>): a filter that is declared and then ignored aggregates <em>every</em> child
/// instead of the declared subset, which is the same silent-wrong-value outcome the whole feature was refused
/// for before #21.
/// </para>
/// </remarks>
public sealed record RollupSchema
{
    /// <summary>Gets the child entity whose records are aggregated.</summary>
    public required string From { get; init; }

    /// <summary>Gets the aggregate operation applied to the child records.</summary>
    public required RollupOperation Op { get; init; }

    /// <summary>
    /// Gets the child field being aggregated, or <see langword="null"/> for
    /// <see cref="RollupOperation.Count"/>, which aggregates rows rather than values.
    /// </summary>
    public string? Field { get; init; }

    /// <summary>
    /// Gets the child's foreign-key field pointing back to this parent — always resolved, never inferred
    /// below this type. See the type's own remarks for why it is required here and optional in the descriptor.
    /// </summary>
    public required string Via { get; init; }
}

/// <summary>The aggregate operation of a <see cref="RollupSchema"/> — the five the frozen schema allows.</summary>
/// <remarks>
/// A separate enum from the descriptor's <c>RollupOp</c>, for the reason <see cref="FieldType"/> is separate
/// from the descriptor's own field-type enum: the applied schema is the artifact a storage driver reads and a
/// <c>schema_json</c> column persists, so it must not move when a descriptor-facing enum gains a member.
/// Members are therefore only ever <b>appended</b> — the applied schema round-trips through
/// System.Text.Json's default numeric enum representation, exactly as <see cref="FieldType"/> and
/// <see cref="OnDelete"/> already do, so reordering one would silently re-read every stored schema.
/// </remarks>
public enum RollupOperation
{
    /// <summary>Sum of the aggregated child field.</summary>
    Sum,

    /// <summary>Count of matching child records; needs no <see cref="RollupSchema.Field"/>.</summary>
    Count,

    /// <summary>Arithmetic mean of the aggregated child field.</summary>
    Avg,

    /// <summary>Minimum of the aggregated child field.</summary>
    Min,

    /// <summary>Maximum of the aggregated child field.</summary>
    Max,
}
