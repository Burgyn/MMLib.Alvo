using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using System.Collections;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>A rendered SQL fragment and the values its named parameters bind.</summary>
/// <param name="Sql">The fragment text.</param>
/// <param name="Parameters">The values <paramref name="Sql"/> references by name.</param>
internal sealed record RenderedSql(string Sql, IReadOnlyDictionary<string, BoundValue> Parameters);

/// <summary>
/// Renders a caller's <see cref="AlvoFilter"/> tree to SQL: every field through the driver's
/// <see cref="IFieldSqlRenderer"/>, every value as a named bind parameter, and the operator taken from a
/// closed allow-list — never assembled from caller text.
/// </summary>
/// <remarks>
/// <para>
/// Rendered rather than composed as a LINQ tree on purpose. EF translates C# equality with C# null
/// semantics, adding an <c>OR x IS NULL</c> compensation term, which would make <c>neq</c> match a
/// <see langword="null"/> field — the opposite of what <see cref="AlvoFilterOperator"/> documents ("a
/// <see langword="null"/> column never satisfies <c>neq</c> either"). Rendering the fragment makes the
/// semantics SQL's own three-valued logic by construction, on every engine, with nothing to compensate
/// for.
/// </para>
/// <para>
/// A caller-supplied pattern's <c>%</c> and <c>_</c> are meaningful and are <b>not</b> escaped — that is
/// PostgREST's own <c>like</c>/<c>ilike</c> semantics, and therefore the semantics an agent expects. It
/// is not an injection surface: the pattern is always a bind parameter, never text in the statement.
/// </para>
/// <para>
/// Every ordering and equality comparison renders <b>both</b> operands through
/// <see cref="IFieldSqlRenderer.RenderComparableOperands"/> at the column's own
/// <see cref="CelValueType"/>, exactly as the core's CEL predicate renderer does. Without it a filter over a
/// <c>decimal</c> is lexicographic on SQLite — <c>price=gt.100</c> matches a row priced 12.34 — which is the
/// same fail-open a rule had, in a second channel. Pattern operators are deliberately not routed: a
/// <c>LIKE</c> is a string operation by definition, and repairing it numerically would not be one.
/// </para>
/// </remarks>
internal static class FilterSqlRenderer
{
    /// <summary>
    /// A malformed filter is refused as an <see cref="ArgumentException"/>, not as an authorization failure.
    /// </summary>
    /// <remarks>
    /// It used to be <see cref="AlvoAuthorizationException"/>, which made <c>status=is.hello</c> — an ordinary
    /// agent typo — read as "not authorized": a 403 with no fix suggestion, in a framework whose principle 4
    /// is structured errors <em>with</em> fix suggestions. Nothing is being hidden and no permission is in
    /// question; the query's shape is wrong, which is the same channel the depth and breadth caps already use.
    /// </remarks>
    private static ArgumentException Malformed(string what, string fix) =>
        new($"The filter {what}. {fix}");

    /// <summary>Renders one caller filter into a SQL fragment and its bound values.</summary>
    /// <remarks>
    /// The depth cap runs first and is also the tree's well-formedness check — it walks iteratively and
    /// rejects a <see langword="null"/> child — so nothing below it can dereference one or recurse into a
    /// tree deep enough to exhaust the stack.
    /// </remarks>
    /// <param name="filter">The caller's filter tree.</param>
    /// <param name="entity">The entity being filtered, as the applied schema declares it.</param>
    /// <param name="fields">The driver's field/expression renderer.</param>
    /// <param name="parameterPrefix">The reserved prefix this fragment's parameters are named from.</param>
    internal static RenderedSql Render(
        AlvoFilter filter, EntitySchema entity, IFieldSqlRenderer fields, string parameterPrefix)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);
        AlvoFilter.EnsureWithinLimits(filter);

        var bag = new ParameterBag(parameterPrefix);
        var sql = Node(filter, entity, fields, bag);
        return new RenderedSql(sql, bag.Values);
    }

    private static string Node(AlvoFilter node, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag) => node switch
    {
        AlvoComparison comparison => Comparison(comparison, entity, fields, bag),
        AlvoAnd and => Connective(and.Filters, entity, fields, bag, "AND", fields.RenderBooleanPredicate(true)),
        AlvoOr or => Connective(or.Filters, entity, fields, bag, "OR", fields.RenderBooleanPredicate(false)),
        AlvoNot not => $"(NOT {Operand(not.Filter, entity, fields, bag)})",
        _ => throw new InvalidOperationException(
            $"'{node.GetType().Name}' is not a known {nameof(AlvoFilter)} case, so it cannot be rendered."),
    };

    /// <summary>
    /// An empty conjunction is the identity of <c>AND</c> (match everything) and an empty disjunction the
    /// identity of <c>OR</c> (match nothing) — spelled out because a <c>WHERE</c> clause has no empty
    /// form, and because guessing the other way round for <c>OR</c> would silently widen a filter.
    /// </summary>
    private static string Connective(
        IReadOnlyList<AlvoFilter> children, EntitySchema entity, IFieldSqlRenderer fields,
        ParameterBag bag, string keyword, string identity)
        => children.Count == 0
            ? identity
            : string.Join($" {keyword} ", children.Select(child => Operand(child, entity, fields, bag)));

    /// <summary>
    /// Every operand of a connective or a negation is fully parenthesised, so operator precedence never
    /// decides what a filter means and a rendered subtree cannot bind loosely into its parent. The
    /// connective itself adds no outer parentheses — the statement composer parenthesises the whole filter
    /// term before <c>AND</c>-ing it onto the policy predicate, which is what keeps a caller's terms from
    /// ever reaching the policy term's nesting level.
    /// </summary>
    private static string Operand(AlvoFilter node, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag) =>
        $"({Node(node, entity, fields, bag)})";

    private static string Comparison(
        AlvoComparison comparison, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var declared = QueryFieldGuard.DeclaredField(entity, comparison.Field);
        var target = new ComparisonTarget(
            declared.Name, fields.RenderField(entity, declared.Name), CelFieldType.Of(declared));

        return comparison.Operator switch
        {
            AlvoFilterOperator.Eq => Ordered(target, "=", comparison.Value, fields, bag),
            AlvoFilterOperator.Neq => Ordered(target, "<>", comparison.Value, fields, bag),
            AlvoFilterOperator.Gt => Ordered(target, ">", comparison.Value, fields, bag),
            AlvoFilterOperator.Gte => Ordered(target, ">=", comparison.Value, fields, bag),
            AlvoFilterOperator.Lt => Ordered(target, "<", comparison.Value, fields, bag),
            AlvoFilterOperator.Lte => Ordered(target, "<=", comparison.Value, fields, bag),
            AlvoFilterOperator.Like => $"{target.Sql} LIKE {bag.Add(fields, target.Column, comparison.Value)}",
            AlvoFilterOperator.ILike => fields.RenderCaseInsensitiveLike(
                target.Sql, bag.Add(fields, target.Column, comparison.Value)),
            AlvoFilterOperator.In => Membership(target, comparison.Value, fields, bag),
            AlvoFilterOperator.Is => Identity(target.Sql, comparison.Value, fields),
            _ => throw Malformed(
                $"uses operator '{comparison.Operator}', which is not one this provider renders",
                $"Use one of {nameof(AlvoFilterOperator)}'s declared members."),
        };
    }

    /// <summary>
    /// One comparison's column, in the three forms every operator needs it: the declared field name the value
    /// binds through, the rendered SQL operand, and the type the comparison is evaluated at. Carried together
    /// so no operator can render the column while forgetting to bind through it.
    /// </summary>
    private sealed record ComparisonTarget(string Column, string Sql, CelValueType Type);

    /// <summary>
    /// One comparison whose answer depends on how the engine <em>orders</em> the operands, so both sides are
    /// repaired at the column's type. Wrapping one side only is worse than wrapping neither: on SQLite every
    /// <c>TEXT</c> value sorts above every numeric one, so a repaired column against an unrepaired parameter
    /// answers a different wrong question.
    /// </summary>
    private static string Ordered(
        ComparisonTarget target, string op, object? value, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var (left, right) = fields.RenderComparableOperands(
            target.Sql, bag.Add(fields, target.Column, value), target.Type);
        return $"{left} {op} {right}";
    }

    /// <summary>
    /// Membership is a set of equality comparisons sharing one left operand, so each candidate is paired with
    /// the column through the dialect's value repair — the repaired column comes back identically from every
    /// pairing, which is what lets one <c>IN</c> list stand for all of them.
    /// </summary>
    private static string Membership(
        ComparisonTarget target, object? value, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var pairs = Candidates(value)
            .Select(candidate => fields.RenderComparableOperands(
                target.Sql, bag.Add(fields, target.Column, candidate), target.Type))
            .ToList();

        return pairs.Count == 0
            ? fields.RenderBooleanPredicate(false)
            : $"{pairs[0].Left} IN ({string.Join(", ", pairs.Select(pair => pair.Right))})";
    }

    /// <summary>
    /// The candidate list. A bare <see cref="string"/> is itself an <see cref="IEnumerable"/>, so it is
    /// refused rather than expanded into one parameter per character, and a scalar is refused rather than
    /// treated as a one-element list — a filter this provider cannot render must fail, never be guessed at.
    /// </summary>
    private static IEnumerable<object?> Candidates(object? value) =>
        value is IEnumerable candidates and not string
            ? candidates.Cast<object?>()
            : throw Malformed(
                "uses 'in' with a value that is not a list",
                "Pass a collection of candidates; a bare string or a scalar is not one.");

    /// <summary>
    /// The one operator that is definitely true or false over a <see langword="null"/> field. Only the three
    /// values SQL's own <c>IS</c> accepts are permitted; anything else is refused rather than coerced, and
    /// none of them binds a parameter.
    /// </summary>
    /// <remarks>
    /// <c>IS NULL</c> is universal SQL and is spelled here. <c>IS TRUE</c>/<c>IS FALSE</c> are <b>not</b>:
    /// T-SQL — which §0 principle 3 names, through Azure SQL — has no boolean type and cannot parse either.
    /// So they are composed from the dialect's own boolean literal and its two-valued fold, which is exactly
    /// what that seam exists for, and the semantics are identical by construction:
    /// <c>COALESCE(x = TRUE, FALSE)</c> is true precisely when <c>x IS TRUE</c> is, a <c>NULL</c> <c>x</c>
    /// included. That keeps <c>is</c> definite on every dialect without inventing a port member for it.
    /// </remarks>
    private static string Identity(string field, object? value, IFieldSqlRenderer fields) => value switch
    {
        null => $"{field} IS NULL",
        true => fields.RenderTwoValued($"{field} = {fields.TrueLiteral}"),
        false => fields.RenderTwoValued($"{field} = {fields.FalseLiteral}"),
        _ => throw Malformed(
            "uses 'is' with a value other than null, true or false",
            "SQL's own IS accepts only those three; compare with 'eq' instead."),
    };

    private sealed class ParameterBag(string prefix)
    {
        private readonly Dictionary<string, BoundValue> _values = new(StringComparer.Ordinal);

        internal IReadOnlyDictionary<string, BoundValue> Values => _values;

        /// <summary>
        /// Records one caller-supplied value against the column it is compared with, and returns the marker
        /// that references it. The column is required rather than optional: a filter operand is the
        /// caller-supplied value this data path binds most often, and binding it by its own CLR type is a
        /// silent wrong answer.
        /// </summary>
        internal string Add(IFieldSqlRenderer fields, string column, object? value)
        {
            var name = prefix + _values.Count.ToString(CultureInfo.InvariantCulture);
            _values[name] = BoundValue.ForColumn(column, value);
            return fields.RenderParameter(name);
        }
    }
}
