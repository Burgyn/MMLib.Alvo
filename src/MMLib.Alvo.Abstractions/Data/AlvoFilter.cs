namespace MMLib.Alvo.Data;

/// <summary>
/// A caller-supplied query filter — the nested boolean tree PostgREST-style query strings
/// compile down to. Applied by an <see cref="IAlvoData"/> implementation <em>in addition to</em>
/// the resolved policy predicate, never instead of it: a filter can only narrow the rows a
/// caller already may see, never widen them (spec §4, the "user filter cannot widen the policy
/// predicate" acceptance criterion).
/// </summary>
public abstract record AlvoFilter;

/// <summary>A single field comparison, e.g. <c>owner_id.eq.&lt;value&gt;</c>.</summary>
/// <param name="Field">The field being compared.</param>
/// <param name="Operator">The comparison operator.</param>
/// <param name="Value">The value to compare against; its runtime type must match <paramref name="Field"/>'s.</param>
public sealed record AlvoComparison(string Field, AlvoFilterOperator Operator, object? Value) : AlvoFilter;

/// <summary>The conjunction of every nested filter — all must match.</summary>
/// <param name="Filters">The nested filters, every one of which must match.</param>
public sealed record AlvoAnd(IReadOnlyList<AlvoFilter> Filters) : AlvoFilter;

/// <summary>The disjunction of every nested filter — at least one must match.</summary>
/// <param name="Filters">The nested filters, at least one of which must match.</param>
public sealed record AlvoOr(IReadOnlyList<AlvoFilter> Filters) : AlvoFilter;

/// <summary>The negation of a nested filter.</summary>
/// <param name="Filter">The filter to negate.</param>
public sealed record AlvoNot(AlvoFilter Filter) : AlvoFilter;

/// <summary>
/// The comparison operators an <see cref="AlvoComparison"/> may use, named after their
/// PostgREST wire-format counterparts (<c>eq</c>, <c>neq</c>, ...) so the HTTP layer (PR3) maps
/// a query string operator straight across with no translation table of its own.
/// </summary>
public enum AlvoFilterOperator
{
    /// <summary>Equal to (<c>eq</c>).</summary>
    Eq,

    /// <summary>Not equal to (<c>neq</c>).</summary>
    Neq,

    /// <summary>Greater than (<c>gt</c>).</summary>
    Gt,

    /// <summary>Greater than or equal to (<c>gte</c>).</summary>
    Gte,

    /// <summary>Less than (<c>lt</c>).</summary>
    Lt,

    /// <summary>Less than or equal to (<c>lte</c>).</summary>
    Lte,

    /// <summary>Case-sensitive pattern match, <c>%</c>/<c>_</c> wildcards (<c>like</c>).</summary>
    Like,

    /// <summary>Case-insensitive pattern match, <c>%</c>/<c>_</c> wildcards (<c>ilike</c>).</summary>
    ILike,

    /// <summary>Membership in a supplied list of values (<c>in</c>).</summary>
    In,

    /// <summary>An identity test against <see langword="null"/>, <see langword="true"/>, or <see langword="false"/> (<c>is</c>).</summary>
    Is,
}
