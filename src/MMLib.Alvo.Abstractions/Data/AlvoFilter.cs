using System.Collections;

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

    /// <summary>
    /// The deepest an <see cref="AlvoFilter"/> tree may nest, counted as the number of nodes on the
    /// longest root-to-leaf path. Every backend that renders or evaluates a filter walks it
    /// recursively, so an uncapped tree from PR3's query-string parser is a denial of service against
    /// the process itself — a <see cref="StackOverflowException"/> no <c>catch</c> can contain. This is
    /// the <see cref="AlvoFilter"/> counterpart of the CEL compiler's own tree-depth cap, and it is
    /// deliberately tighter (a filter's breadth carries the term count; only genuine nesting counts
    /// towards this, so 32 levels is far past any query string a human or agent writes on purpose).
    /// </summary>
    /// <remarks>
    /// Deliberately a get-only property, not a <see langword="const"/>. A public
    /// <see langword="const"/> is inlined at each consumer's compile time, so every driver, every
    /// query-string parser and every host comparing against it would bake the literal in — and the
    /// cap could then never be changed, nor made configurable, without recompiling all of them,
    /// while a consumer compiled against the old value and a framework enforcing a new one would
    /// disagree silently. A property keeps every current call site source-compatible and leaves an
    /// options-backed cap open as a purely internal change.
    /// </remarks>
    public static int MaxDepth { get; } = 32;

    /// <summary>
    /// The most nodes an <see cref="AlvoFilter"/> tree may carry in total — every comparison plus every
    /// connective, however they are nested. This is the <b>breadth</b> counterpart of
    /// <see cref="MaxDepth"/>, which a wide-but-shallow tree escapes entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured, not guessed. A rendered <c>AND</c>/<c>OR</c> chain nests SQLite's parser once per term, so
    /// its default expression-tree ceiling of 1000 is the hard wall: 900 terms answered in 14 ms and
    /// <b>1000 threw a raw <c>SqliteException</c></b> — while the identical filter answered on PostgreSQL,
    /// which has no such ceiling. Same caller input, an unhandled provider exception on one engine and an
    /// answer on the other: §0 principle 3, on the channel a caller controls per request, and the third
    /// instance of the class the NUL refusal and the UTC normalisation each closed <em>per value</em>.
    /// </para>
    /// <para>
    /// 256 sits far enough below 1000 to leave room for the policy predicate's own terms in the same
    /// statement, and is far past any query string a human or agent writes on purpose; 256 terms answered in
    /// 23 ms. It also caps the statement text a caller can make the server compose, which PostgreSQL
    /// otherwise leaves entirely to them.
    /// </para>
    /// </remarks>
    public static int MaxTerms { get; } = 256;

    /// <summary>
    /// The most candidates one <see cref="AlvoFilterOperator.In"/> comparison may list. Each candidate
    /// becomes its own bind parameter, so this is a limit on the statement, not on the tree.
    /// </summary>
    /// <remarks>
    /// Measured on SQLite: 1000 candidates answered in 14 ms, 20 000 took 2.0 s and 32 000 took 4.8 s
    /// <em>composing and parsing</em> before answering, and 40 000 threw <c>too many SQL variables</c> after
    /// 3.5 s — where PostgreSQL answered 40 000 in 0.27 s. 1000 per list keeps a whole statement's parameter
    /// count inside SQLite's own 32 766 ceiling even with several lists, and keeps the composition cost off
    /// the caller's control.
    /// </remarks>
    public static int MaxInCandidates { get; } = 1000;

    /// <summary>
    /// Throws when <paramref name="filter"/> exceeds any of this port's structural limits —
    /// <see cref="MaxDepth"/>, <see cref="MaxTerms"/>, <see cref="MaxInCandidates"/> — or is malformed.
    /// Every <see cref="IAlvoData"/> implementation must call this before walking a caller's filter.
    /// </summary>
    /// <remarks>
    /// <b>One entry point on purpose.</b> This replaced a depth-only guard that every implementation called
    /// faithfully while nothing capped breadth at all; two guards would be two things to remember, and the
    /// one a driver author forgets is the one that was added last. The measurement is a single iterative
    /// walk, so the check itself cannot overflow on the tree it is about to reject, and an
    /// <see cref="AlvoFilterOperator.In"/> list is counted with an early exit so a lazily-generated
    /// candidate sequence cannot be walked forever on the way to being refused.
    /// </remarks>
    /// <param name="filter">The tree to check, or <see langword="null"/> for no filter.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="filter"/> nests deeper than <see cref="MaxDepth"/>, carries more than
    /// <see cref="MaxTerms"/> nodes, lists more than <see cref="MaxInCandidates"/> candidates in one
    /// <c>in</c>, or carries a <see langword="null"/> where a child belongs.
    /// </exception>
    public static void EnsureWithinLimits(AlvoFilter? filter)
    {
        if (Exceeded(Measure(filter)) is { } message)
        {
            throw new ArgumentException(message, nameof(filter));
        }
    }

    /// <summary>
    /// The first limit <paramref name="shape"/> exceeds, as the message a caller reads, or
    /// <see langword="null"/> when it exceeds none. Composed rather than thrown so every refusal is raised at
    /// one site, naming the argument the caller actually passed.
    /// </summary>
    private static string? Exceeded(FilterShape shape) =>
        Over(shape.Depth, MaxDepth, $"nests {shape.Depth} levels deep",
            "Flatten the nesting — an and/or over many terms is one level, not one level per term.")
        ?? Over(shape.Terms, MaxTerms, $"carries {shape.Terms} terms",
            "Narrow the query — a filter this wide is a statement the engine may refuse outright.")
        ?? Over(shape.InCandidates, MaxInCandidates, $"lists {shape.InCandidates} 'in' candidates",
            "Split the list across requests — every candidate becomes its own bind parameter.");

    private static string? Over(int measured, int limit, string what, string fix) =>
        measured > limit ? $"The filter {what}, exceeding the maximum of {limit}. {fix}" : null;

    /// <summary>
    /// Enumerates every field name any <see cref="AlvoComparison"/> in this tree compares, in no
    /// particular order and with duplicates. Lives here, on the closed hierarchy itself, rather than
    /// in each <see cref="IAlvoData"/> implementation: every implementation has to validate a
    /// caller's filter fields against the schema and against
    /// <see cref="Rules.PolicyDecision.HiddenFields"/> before touching a row, and a per-provider copy
    /// of a security-relevant tree walk is exactly the kind of divergence this hierarchy was closed to
    /// prevent. Walks iteratively, so a hostile tree cannot exhaust the stack here on the way to being
    /// rejected for being too deep.
    /// </summary>
    /// <param name="filter">The tree to walk, or <see langword="null"/> for no filter.</param>
    /// <exception cref="ArgumentException"><paramref name="filter"/> carries a <see langword="null"/> where a child belongs.</exception>
    public static IEnumerable<string> ReferencedFields(AlvoFilter? filter)
    {
        if (filter is null)
        {
            yield break;
        }

        var pending = new Stack<AlvoFilter>();
        pending.Push(filter);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is AlvoComparison comparison)
            {
                yield return comparison.Field;
            }

            foreach (var child in Children(node))
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>Everything one iterative walk of the tree measures.</summary>
    /// <param name="Depth">The number of nodes on the longest root-to-leaf path.</param>
    /// <param name="Terms">The total number of nodes.</param>
    /// <param name="InCandidates">The longest <c>in</c> candidate list any comparison carries.</param>
    private sealed record FilterShape(int Depth, int Terms, int InCandidates);

    /// <summary>
    /// One walk for all three limits. Separate walks would each have to be bounded separately, and the
    /// unbounded one is the one a hostile tree finds.
    /// </summary>
    private static FilterShape Measure(AlvoFilter? filter)
    {
        if (filter is null)
        {
            return new FilterShape(0, 0, 0);
        }

        var pending = new Stack<(AlvoFilter Node, int Depth)>();
        pending.Push((filter, 1));
        var deepest = 0;
        var terms = 0;
        var widestInList = 0;

        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            deepest = Math.Max(deepest, depth);
            terms++;
            widestInList = Math.Max(widestInList, CandidateCount(node));

            foreach (var child in Children(node))
            {
                pending.Push((child, depth + 1));
            }
        }

        return new FilterShape(deepest, terms, widestInList);
    }

    /// <summary>
    /// How many candidates an <c>in</c> comparison lists, counted no further than one past the cap.
    /// </summary>
    /// <remarks>
    /// A candidate list is caller-supplied and may be a lazily-generated sequence, so counting it to the end
    /// would let a hostile one run forever inside the guard that exists to reject it. A collection answers
    /// from its own <c>Count</c>; anything else is enumerated only until the cap is provably exceeded.
    /// </remarks>
    private static int CandidateCount(AlvoFilter node)
    {
        if (node is not AlvoComparison { Operator: AlvoFilterOperator.In } comparison
            || comparison.Value is not IEnumerable candidates
            || comparison.Value is string)
        {
            return 0;
        }

        if (candidates is ICollection collection)
        {
            return collection.Count;
        }

        var counted = 0;
        foreach (var _ in candidates)
        {
            if (++counted > MaxInCandidates)
            {
                break;
            }
        }

        return counted;
    }

    /// <summary>
    /// A node's direct children, for both walks above. Every case is named explicitly and an
    /// unrecognized one throws rather than reporting "no children": the hierarchy is closed
    /// (<see langword="private protected"/>), so this can only be reached by adding a case to this file
    /// without updating this method — and a silently childless node would hide its whole subtree from
    /// the depth cap and its comparisons from the field walk.
    /// </summary>
    private static IReadOnlyList<AlvoFilter> Children(AlvoFilter node) => node switch
    {
        AlvoComparison => [],
        AlvoAnd and => WellFormed(and.Filters),
        AlvoOr or => WellFormed(or.Filters),
        AlvoNot not => [WellFormed(not.Filter)],
        _ => throw new InvalidOperationException(
            $"'{node.GetType().Name}' is not a known {nameof(AlvoFilter)} case; its subtree cannot be walked."),
    };

    /// <summary>
    /// Rejects a <see langword="null"/> child before either walk dereferences it. The four cases are
    /// positional records with no null guard of their own, so an <see cref="AlvoAnd"/> built with a
    /// <see langword="null"/> list — or a list with a <see langword="null"/> in it — would otherwise
    /// leave a <see cref="NullReferenceException"/> escaping the one check every backend must run
    /// before touching a row, which is a far worse signal than a rejection.
    /// </summary>
    private static IReadOnlyList<AlvoFilter> WellFormed(IReadOnlyList<AlvoFilter> children)
    {
        EnsureNotNull(children);
        foreach (var child in children)
        {
            EnsureNotNull(child);
        }

        return children;
    }

    private static AlvoFilter WellFormed(AlvoFilter child)
    {
        EnsureNotNull(child);
        return child;
    }

    /// <summary>
    /// Named <c>filter</c> to match the parameter of the two public entry points, so the rejection a
    /// caller sees names the argument they actually passed rather than an internal child.
    /// </summary>
    /// <param name="filter">The nested filter to check.</param>
    private static void EnsureNotNull(object? filter)
    {
        if (filter is null)
        {
            throw new ArgumentException(MalformedFilterMessage, nameof(filter));
        }
    }

    private const string MalformedFilterMessage =
        "The filter is malformed: a nested filter is null. Every and/or/not must carry real children.";
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
    /// <remarks>
    /// <b>Case-sensitive on every engine</b>, which is standard SQL's meaning. That is a real obligation on an
    /// implementation rather than a description of what engines happen to do: SQLite's <c>LIKE</c> is
    /// ASCII-case-<em>in</em>sensitive by default, so its driver has to turn that off
    /// (<c>PRAGMA case_sensitive_like = ON</c>) or the same filter answers differently there than on
    /// PostgreSQL — silently, and with a superset of the rows.
    /// </remarks>
    Like,

    /// <summary>Case-insensitive pattern match, <c>%</c>/<c>_</c> wildcards (<c>ilike</c>).</summary>
    /// <remarks>
    /// The guarantee is <b>ASCII</b> case-insensitivity on every engine. Folding beyond ASCII is deliberately
    /// <em>not</em> guaranteed, because it follows the host database's own collation: PostgreSQL's <c>ILIKE</c>
    /// folds <c>čé</c> and SQLite's <c>UPPER</c>-based emulation does not. A caller who needs
    /// Unicode-correct matching must not rely on this operator for it.
    /// </remarks>
    ILike,

    /// <summary>Membership in a supplied list of values (<c>in</c>).</summary>
    In,

    /// <summary>An identity test against <see langword="null"/>, <see langword="true"/>, or <see langword="false"/> (<c>is</c>).</summary>
    Is,
}
