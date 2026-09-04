using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Parses the recursive half of the filter grammar: an <c>or=(…)</c>/<c>and=(…)</c> group, the
/// <c>not.</c> prefix, and the single member form both a group's children and a query-string key reduce to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The depth cap is checked before each descent, never on the finished tree.</b> A ten-thousand-deep group
/// must be refused without ten thousand stack frames, or the parser is one fuzz case away from a
/// <see cref="StackOverflowException"/> — which is not an exception a request pipeline can turn into a 500,
/// it is the process ending. Recursion is kept (it is the clearest expression of a recursive grammar) and
/// bounded instead: no descent begins past <see cref="AlvoFilter.MaxDepth"/>, so the deepest possible stack
/// is that many frames whatever the caller sends.
/// </para>
/// <para>
/// <b>One member form, so the key form and the group form cannot diverge.</b> <c>?not.color=eq.red</c> and
/// the member <c>not.color.eq.red</c> inside a group are the same filter written two ways, and PostgREST's own
/// grammar treats them so; parsing them through one function is what keeps a term that is refused at the top
/// level from being accepted one level down.
/// </para>
/// <para>
/// <b>Deviation from PostgREST, stated:</b> a nested group is spelled <c>and=(…)</c>/<c>or=(…)</c> — with the
/// <c>=</c> — inside a group as well as at the top level, which is the spelling this feature's design table
/// fixes. PostgREST itself writes the nested form without the <c>=</c> (<c>or=(a.eq.1,and(b.eq.2))</c>). The
/// design's spelling is used verbatim rather than "corrected" here, because one grammar in one place beats a
/// parser that quietly accepts a second dialect; widening to PostgREST's exact nested form later is additive.
/// </para>
/// </remarks>
internal static class FilterGroupParser
{
    private const string OrGroupPrefix = ReservedQueryKeys.Or + "=";

    private const string AndGroupPrefix = ReservedQueryKeys.And + "=";

    /// <summary>
    /// Parses one member of a group — a term, a nested group, or either of those negated.
    /// </summary>
    /// <param name="member">The caller-supplied member text.</param>
    /// <param name="scope">The request's resolvable fields and node budget.</param>
    /// <param name="depth">How many filter nodes already sit above this one.</param>
    /// <param name="filter">The parsed filter.</param>
    /// <param name="violation">Why the member was refused.</param>
    internal static bool TryParseMember(
        string member, FilterParseScope scope, int depth, out AlvoFilter? filter, out AlvoViolation? violation)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(scope);

        filter = null;
        violation = null;
        var (negated, bare) = SplitNegation(member);

        if (negated && !TryDescend(scope, depth, out violation))
        {
            return false;
        }

        if (!TryParseBareMember(bare, scope, Below(depth, negated), out var parsed, out violation))
        {
            return false;
        }

        filter = Negated(parsed!, negated);
        return true;
    }

    /// <summary>
    /// Parses one <b>query-string key</b> and its value — the top-level form, where the negation prefix and the
    /// group keyword arrive already separated from the value by the <c>=</c>.
    /// </summary>
    /// <remarks>
    /// Kept distinct from <see cref="TryParseMember"/> rather than re-joined into member text, because joining
    /// re-introduces exactly the ambiguity the member form has and the key form does not: <c>?not=eq.x</c>
    /// joined becomes <c>not.eq.x</c>, which reads as a negation of a term over a field called <c>eq</c>. The
    /// two forms share every rule below this point and differ only in how they are split.
    /// </remarks>
    /// <param name="name">The key with any <c>not.</c> prefix already removed.</param>
    /// <param name="value">The key's value.</param>
    /// <param name="negated">Whether the key carried the negation prefix.</param>
    /// <param name="scope">The request's resolvable fields and node budget.</param>
    /// <param name="depth">How many filter nodes already sit above this one.</param>
    /// <param name="filter">The parsed filter.</param>
    /// <param name="violation">Why the parameter was refused.</param>
    internal static bool TryParseNamed(
        string name,
        string value,
        bool negated,
        FilterParseScope scope,
        int depth,
        out AlvoFilter? filter,
        out AlvoViolation? violation)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(scope);

        filter = null;
        violation = null;
        if (negated && !TryDescend(scope, depth, out violation))
        {
            return false;
        }

        if (!TryParseBareNamed(name, value, scope, Below(depth, negated), out var parsed, out violation))
        {
            return false;
        }

        filter = Negated(parsed!, negated);
        return true;
    }

    private static bool TryParseBareNamed(
        string name,
        string value,
        FilterParseScope scope,
        int depth,
        out AlvoFilter? filter,
        out AlvoViolation? violation) =>
        name is ReservedQueryKeys.Or or ReservedQueryKeys.And
            ? TryParseGroup(value, name == ReservedQueryKeys.And, scope, depth, out filter, out violation)
            : FilterTermParser.TryParse(name, value, scope, out filter, out violation);

    /// <summary>
    /// A negation is a node of its own, so its operand sits one level deeper — charged by
    /// <see cref="TryDescend"/> before the operand is even looked at.
    /// </summary>
    private static int Below(int depth, bool negated) => negated ? depth + 1 : depth;

    private static AlvoFilter Negated(AlvoFilter filter, bool negated) => negated ? new AlvoNot(filter) : filter;

    /// <summary>
    /// Strips the negation prefix, if any, from a name or a group member. Exactly <b>one</b> <c>not.</c> is ever
    /// removed — <c>not.not.x.eq.1</c> is not in the grammar, and reading it as a double negation would invent a
    /// spelling. One implementation because the rule is one rule; the two <em>splits</em> below it (a key from its
    /// value, a member from its operator) genuinely differ and stay apart.
    /// </summary>
    /// <param name="text">The key or member as the caller wrote it.</param>
    internal static (bool Negated, string Bare) SplitNegation(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.StartsWith(ReservedQueryKeys.NotPrefix, StringComparison.Ordinal)
            ? (true, text[ReservedQueryKeys.NotPrefix.Length..])
            : (false, text);
    }

    /// <summary>
    /// A group member with its negation prefix already removed. Exactly one <c>not.</c> is ever stripped:
    /// <c>not.not.x.eq.1</c> is not in the grammar, and reading it as a double negation would invent a
    /// spelling.
    /// </summary>
    private static bool TryParseBareMember(
        string bare, FilterParseScope scope, int depth, out AlvoFilter? filter, out AlvoViolation? violation) =>
        TrySplitGroupKeyword(bare, out var conjunction, out var list)
            ? TryParseGroup(list, conjunction, scope, depth, out filter, out violation)
            : TryParseTerm(bare, scope, out filter, out violation);

    private static bool TrySplitGroupKeyword(string bare, out bool conjunction, out string list)
    {
        if (bare.StartsWith(AndGroupPrefix, StringComparison.Ordinal))
        {
            (conjunction, list) = (true, bare[AndGroupPrefix.Length..]);
            return true;
        }

        if (bare.StartsWith(OrGroupPrefix, StringComparison.Ordinal))
        {
            (conjunction, list) = (false, bare[OrGroupPrefix.Length..]);
            return true;
        }

        (conjunction, list) = (false, string.Empty);
        return false;
    }

    private static bool TryParseGroup(
        string list,
        bool conjunction,
        FilterParseScope scope,
        int depth,
        out AlvoFilter? filter,
        out AlvoViolation? violation)
    {
        filter = null;
        if (!TryDescend(scope, depth, out violation))
        {
            return false;
        }

        var split = ParenthesisedList.Split(list, AlvoFilter.MaxTerms, out var members);
        if (split != ListSplit.Ok)
        {
            violation = split == ListSplit.TooMany
                ? QueryViolations.FilterTooWide()
                : QueryViolations.MalformedGroup();
            return false;
        }

        if (!TryParseMembers(members, scope, depth + 1, out var children, out violation))
        {
            return false;
        }

        filter = conjunction ? new AlvoAnd(children!) : new AlvoOr(children!);
        return true;
    }

    private static bool TryParseMembers(
        IReadOnlyList<string> members,
        FilterParseScope scope,
        int depth,
        out IReadOnlyList<AlvoFilter>? children,
        out AlvoViolation? violation)
    {
        children = null;
        var parsed = new List<AlvoFilter>(members.Count);
        foreach (var member in members)
        {
            if (!TryParseMember(member, scope, depth, out var child, out violation))
            {
                return false;
            }

            parsed.Add(child!);
        }

        violation = null;
        children = parsed;
        return true;
    }

    /// <summary>
    /// Splits a member's field name from its <c>&lt;operator&gt;.&lt;value&gt;</c> at the <b>first</b> dot: a
    /// declared field name cannot contain one (the descriptor's own grammar is
    /// <c>^[a-z][a-z0-9_]{0,62}$</c>) and an operator cannot either, while a value routinely does —
    /// <c>2020.5</c>, <c>1.2.3</c>. Splitting from the right would read <c>year.gte.2020.5</c> as a field
    /// called <c>year.gte.2020</c>.
    /// </summary>
    private static bool TryParseTerm(
        string term, FilterParseScope scope, out AlvoFilter? filter, out AlvoViolation? violation)
    {
        var separator = term.IndexOf('.');
        if (separator < 0)
        {
            filter = null;
            violation = QueryViolations.MalformedTerm();
            return false;
        }

        return FilterTermParser.TryParse(
            term[..separator], term[(separator + 1)..], scope, out filter, out violation);
    }

    /// <summary>
    /// The deepest a caller's filter may nest <em>connectives</em> — two levels <b>inside</b>
    /// <see cref="AlvoFilter.MaxDepth"/>, which counts nodes on the longest root-to-leaf path.
    /// </summary>
    /// <remarks>
    /// The two reserved levels are the ones a descent never checks: the <b>comparison leaf</b> below the
    /// innermost connective, and the <b>conjunction</b> <c>QueryStringParser</c> wraps several top-level
    /// parameters in. Leaving either unaccounted would let the parser produce a tree the port then refuses,
    /// moving the failure from a structured 422 to an <c>ArgumentException</c> out of a query. Reserving them
    /// makes <see cref="AlvoFilter.EnsureWithinLimits"/> unreachable from this parser, which is worth far more
    /// than two levels of nesting nobody writes on purpose.
    /// </remarks>
    internal static int MaxNesting { get; } = AlvoFilter.MaxDepth - 2;

    /// <summary>
    /// Whether one more level may be entered — the depth cap and the term budget together, charged for the
    /// node about to be constructed. Depth and breadth are refused with <em>different</em> violations, because
    /// they have different fixes and because a shared one would make neither cap observable.
    /// </summary>
    private static bool TryDescend(FilterParseScope scope, int depth, out AlvoViolation? violation)
    {
        if (depth > MaxNesting)
        {
            violation = QueryViolations.FilterTooDeep();
            return false;
        }

        violation = scope.TryChargeNode() ? null : QueryViolations.FilterTooWide();
        return violation is null;
    }
}
