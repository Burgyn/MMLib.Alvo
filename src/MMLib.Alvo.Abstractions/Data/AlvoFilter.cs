namespace MMLib.Alvo.Data;

/// <summary>
/// A caller-supplied query filter — the nested boolean tree PostgREST-style query strings
/// compile down to. Applied by an <see cref="IAlvoData"/> implementation <em>in addition to</em>
/// the resolved policy predicate, never instead of it: a filter can only narrow the rows a
/// caller already may see, never widen them (spec §4, the "user filter cannot widen the policy
/// predicate" acceptance criterion).
/// </summary>
/// <remarks>
/// The constructor is <see langword="private protected"/>, so only the four cases declared in
/// this assembly (<see cref="AlvoComparison"/>, <see cref="AlvoAnd"/>, <see cref="AlvoOr"/>,
/// <see cref="AlvoNot"/>) may ever derive from it. This hierarchy is closed deliberately: every
/// <see cref="IAlvoData"/> implementation's evaluator/renderer switches over these four cases with
/// a fail-closed default for anything else (an unrecognized filter node must never be treated as
/// "no filter" or, worse, invert into a match under negation) — an open hierarchy would let a
/// third party add a node no backend can render, and closing it after PR3 ships would be a
/// breaking change.
/// </remarks>
public abstract record AlvoFilter
{
    private protected AlvoFilter()
    {
    }
}

/// <summary>A single field comparison, e.g. <c>owner_id.eq.&lt;value&gt;</c>.</summary>
/// <param name="Field">The field being compared.</param>
/// <param name="Operator">The comparison operator.</param>
/// <param name="Value">The value to compare against; its runtime type must match <paramref name="Field"/>'s.</param>
/// <remarks>
/// A positional record: adding a parameter to this constructor later is a binary break for any
/// compiled caller. Unlike <see cref="AlvoQuery"/> (a property-initializer record designed to grow
/// additively), this shape is not meant to grow — a new comparison capability gets a new
/// <see cref="AlvoFilterOperator"/> member or a new <see cref="AlvoFilter"/> case instead.
/// </remarks>
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
/// <remarks>
/// <b>Null semantics follow SQL's three-valued logic, not two-valued boolean logic.</b> Every
/// operator here except <see cref="Is"/> yields <c>UNKNOWN</c> — never a match — when the row's
/// field value is <see langword="null"/>, exactly as SQL's own <c>=</c>/<c>&lt;&gt;</c>/<c>&lt;</c>/
/// <c>&gt;</c>/<c>LIKE</c>/<c>IN</c> do: a <see langword="null"/> column never satisfies <c>neq</c>
/// either, since <c>col &lt;&gt; value</c> is <c>UNKNOWN</c>, not <see langword="true"/>, when
/// <c>col IS NULL</c>. <c>UNKNOWN</c> propagates through a filter tree's <c>AND</c>/<c>OR</c>/
/// <c>NOT</c> exactly as SQL's do (in particular, <c>NOT</c> of an unresolved comparison stays
/// unresolved — it does not flip into a match), and a filter tree only ever matches a row when it
/// resolves to exactly <see langword="true"/>. <see cref="Is"/> is the one operator designed to
/// test a <see langword="null"/>/<see langword="true"/>/<see langword="false"/> field directly and
/// always resolves to a definite <see langword="true"/>/<see langword="false"/>, matching SQL's own
/// <c>IS NULL</c>/<c>IS TRUE</c>/<c>IS FALSE</c>. Every provider implementing <see cref="IAlvoData"/>
/// must preserve this — rendering these operators into SQL directly already gets it for free; an
/// in-memory evaluator must reproduce it deliberately.
/// </remarks>
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
