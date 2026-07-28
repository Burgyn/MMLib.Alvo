using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using System.Collections.Frozen;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The operator allow-list, and the two type rules an operator's own meaning imposes on the field it is
/// applied to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The allow-list is derived from <see cref="AlvoFilterOperator"/>, not written out beside it.</b> That
/// enum's own remarks say its members are named after their PostgREST wire spellings precisely so "the HTTP
/// layer maps a query string operator straight across with no translation table of its own" — and a
/// hand-written table is how the next operator added to the port ships unreachable over HTTP, or reachable
/// under a spelling nobody chose. The spelling is the member name lower-cased, which yields exactly
/// <c>eq neq gt gte lt lte like ilike in is</c>; <c>QueryStringParserTests</c> pins that set, so a future
/// member whose lower-cased name is <em>not</em> its PostgREST spelling fails a test rather than inventing a
/// dialect.
/// </para>
/// <para>
/// An unrecognized spelling is a refusal, never a fallback to <c>eq</c>: a mistyped operator that quietly
/// became equality would answer a different question than the caller asked, and §2.1 names the operator
/// allow-list as one of the two defences against injection through a filter.
/// </para>
/// </remarks>
internal static class FilterOperators
{
    /// <summary>The wire spelling of <paramref name="operator"/>.</summary>
    /// <param name="operator">The port's operator.</param>
    internal static string WireName(AlvoFilterOperator @operator) =>
#pragma warning disable CA1308 // A wire spelling is lower-case by PostgREST's definition, not by a locale's.
        @operator.ToString().ToLowerInvariant();
#pragma warning restore CA1308

    /// <summary>Every wire spelling, in the enum's own declaration order.</summary>
    internal static IReadOnlyList<string> WireNames { get; } =
        [.. Enum.GetValues<AlvoFilterOperator>().Select(WireName)];

    /// <summary>Every wire spelling as a comma-separated list, for a fix suggestion.</summary>
    internal static string AsList { get; } = string.Join(", ", WireNames);

    private static readonly FrozenDictionary<string, AlvoFilterOperator> _byWireName =
        Enum.GetValues<AlvoFilterOperator>().ToFrozenDictionary(WireName, StringComparer.Ordinal);

    /// <summary>
    /// Resolves a caller-supplied spelling. Ordinal and case-sensitive, like every other name in the
    /// framework: <c>EQ</c> is not an operator, and admitting it would be a second spelling for one thing.
    /// </summary>
    /// <param name="token">The caller-supplied operator token.</param>
    /// <param name="operator">The resolved operator.</param>
    internal static bool TryResolve(string token, out AlvoFilterOperator @operator) =>
        _byWireName.TryGetValue(token, out @operator);

    /// <summary>
    /// Whether <paramref name="operator"/> is a pattern match, which is a <b>string</b> operation by
    /// definition and whose operand is therefore a pattern rather than a value of the field's type.
    /// </summary>
    /// <param name="operator">The resolved operator.</param>
    internal static bool IsPatternMatch(AlvoFilterOperator @operator) =>
        @operator is AlvoFilterOperator.Like or AlvoFilterOperator.ILike;

    /// <summary>
    /// Whether <paramref name="operator"/> asks the engine to <em>order</em> the operands rather than only
    /// compare them for equality.
    /// </summary>
    /// <param name="operator">The resolved operator.</param>
    internal static bool IsOrdering(AlvoFilterOperator @operator) => @operator is
        AlvoFilterOperator.Gt or AlvoFilterOperator.Gte or AlvoFilterOperator.Lt or AlvoFilterOperator.Lte;

    /// <summary>
    /// The value types this port defines a total order over — the ones
    /// <c>IFieldSqlRenderer.RenderComparableOperands</c> repairs and the reference evaluator can compare.
    /// </summary>
    /// <remarks>
    /// Applying an ordering operator to any other type is refused rather than answered, because the two
    /// shipped drivers and the in-memory reference would answer it <em>differently</em>: a rendered
    /// <c>uuid &gt; @p</c> is a real comparison on PostgreSQL while the reference evaluator resolves it to
    /// <c>UNKNOWN</c> and returns no row. §0 principle 3 is that one filter behaves identically on every
    /// engine, so the ambiguous cases are closed at the parser instead of diverging below it.
    /// </remarks>
    private static readonly FrozenSet<CelValueType> _orderable =
        new[] { CelValueType.String, CelValueType.Int, CelValueType.Decimal, CelValueType.Timestamp }
            .ToFrozenSet();

    /// <summary>Whether a comparison over <paramref name="type"/> may be ordered.</summary>
    /// <param name="type">The type the comparison is evaluated at.</param>
    internal static bool IsOrderable(CelValueType type) => _orderable.Contains(type);
}
