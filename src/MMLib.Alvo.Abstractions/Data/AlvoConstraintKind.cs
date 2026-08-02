namespace MMLib.Alvo.Data;

/// <summary>
/// Which stored-state constraint a request collided with — the distinction a caller can act on, and the
/// only one <see cref="AlvoConstraintViolationException"/> commits to.
/// </summary>
/// <remarks>
/// Two members rather than one flag or a free-text reason, because the two have <em>different fixes</em> and
/// nothing else about them differs: a <see cref="Unique"/> collision is repaired by sending another value,
/// and a <see cref="Referenced"/> one by removing the records that point at this one. A third member
/// encoding <em>which</em> constraint, or a string carrying the engine's own constraint name, would key the
/// refusal on its reason rather than its kind — the schema-and-data oracle every deny reason in this
/// framework is worded to avoid.
/// </remarks>
public enum AlvoConstraintKind
{
    /// <summary>A value the request supplies is already held by another record on a <c>unique</c> field.</summary>
    Unique,

    /// <summary>
    /// The record cannot be removed while other records still reference it — the refusal a <c>ref</c> field
    /// declaring <c>onDelete: "restrict"</c> asked the store for.
    /// </summary>
    Referenced,
}
