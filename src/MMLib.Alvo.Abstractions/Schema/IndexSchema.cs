namespace MMLib.Alvo.Schema;

/// <summary>Describes an index on one or more fields of an entity.</summary>
/// <param name="Fields">The field names composing the index.</param>
/// <param name="Unique">Whether the index enforces uniqueness.</param>
public sealed record IndexSchema(IReadOnlyList<string> Fields, bool Unique)
{
    /// <summary>
    /// The CEL source of the index's predicate — only the rows it accepts are indexed (a partial index) — or
    /// <see langword="null"/> for a whole-table index. The source, not SQL: each driver renders it when it builds its model.
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// The range no two rows with equal <see cref="Fields"/> may overlap, or <see langword="null"/> for an ordinary index.
    /// An exclusion is not a B-tree index: each driver enforces it through its dialect's exclusion constraint.
    /// </summary>
    public ExclusionSchema? Exclude { get; init; }
}
