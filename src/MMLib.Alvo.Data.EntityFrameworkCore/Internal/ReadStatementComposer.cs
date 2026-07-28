using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>One composed read statement and the values its named parameters bind.</summary>
/// <param name="Sql">The statement text.</param>
/// <param name="Parameters">The values <paramref name="Sql"/> references by name.</param>
internal sealed record ReadStatement(string Sql, IReadOnlyDictionary<string, BoundValue> Parameters);

/// <summary>
/// Composes the one statement every Alvo read goes through: the resolved <c>USING</c> predicate and the
/// synthesized tenant scope, <c>AND</c>-joined in the <c>WHERE</c> clause of a single <c>SELECT</c>, plus
/// whatever the operation adds — the caller's filter, a row id, a keyset cursor, a row lock.
/// </summary>
/// <remarks>
/// <para>
/// Every term is composed here, in one place, so there is exactly one answer to "is the policy predicate
/// in the <c>WHERE</c> clause or applied afterwards" and a snapshot of this string is the proof. The
/// caller's own terms are only ever <c>AND</c>-ed onto a fully parenthesised policy predicate, so they
/// can only narrow the result; nothing a caller supplies can reach the same nesting level as the policy
/// term, let alone be <c>OR</c>-ed beside it.
/// </para>
/// <para>
/// Each of the three predicates a <see cref="PolicyDecision"/> carries is rendered with its own
/// parameter prefix. Renders number their parameters from zero independently, so two default-prefixed
/// predicates in one command would bind two values to one name — and whichever won would silently change
/// what the other predicate means.
/// </para>
/// </remarks>
internal sealed class ReadStatementComposer
{
    private readonly IPredicateRenderer _predicates;
    private readonly IFieldSqlRenderer _fields;
    private readonly IAlvoSqlDialect _dialect;

    internal ReadStatementComposer(IPredicateRenderer predicates, IFieldSqlRenderer fields, IAlvoSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(dialect);
        _predicates = predicates;
        _fields = fields;
        _dialect = dialect;
    }

    /// <summary>What one operation adds to the policy-filtered read.</summary>
    internal sealed record ReadStatementOptions
    {
        /// <summary>The caller's filter, or <see langword="null"/> for none.</summary>
        internal AlvoFilter? Filter { get; init; }

        /// <summary>A single row's id, for a get/pre-image read.</summary>
        internal Guid? RowId { get; init; }

        /// <summary>The keyset cursor anchor, for a page after the first.</summary>
        internal KeysetAnchor? Anchor { get; init; }

        /// <summary>The caller's sort keys, outermost first; empty for none.</summary>
        internal IReadOnlyList<AlvoSort> Sort { get; init; } = [];

        /// <summary>The page's maximum row count, or <see langword="null"/> for no explicit limit.</summary>
        internal int? Limit { get; init; }

        /// <summary>The number of leading rows to skip, or <see langword="null"/> for none.</summary>
        internal int? Offset { get; init; }

        /// <summary>
        /// Whether the projection ignores the decision's field mask. <see langword="true"/> only for the
        /// pre-image a <c>WITH CHECK</c> verdict is reached over: that check evaluates the complete stored
        /// row, and a masked field read as a projected <c>NULL</c> would silently change what a rule
        /// referencing it decides. Masking still applies to everything this data path <em>returns</em> — a
        /// record is masked when it is assembled, so an unmasked read is not an unmasked response.
        /// </summary>
        internal bool Unmasked { get; init; }

        /// <summary>
        /// The mutation this read's row is a pre-image for, or <see langword="null"/> for a read that takes
        /// no lock. It selects the lock <em>mode</em>, not merely whether to lock — an update's pre-image
        /// takes the weaker no-key lock and a delete's takes the full one.
        /// </summary>
        internal PreImageMutation? LockFor { get; init; }
    }

    /// <summary>Composes the policy-filtered read for one operation.</summary>
    /// <param name="entity">The entity being read, as the applied schema declares it.</param>
    /// <param name="decision">The verdict <see cref="IPolicyEngine"/> returned for this caller.</param>
    /// <param name="context">The caller the predicates' context values are resolved against.</param>
    /// <param name="options">What this operation adds to the read.</param>
    /// <param name="rows">
    /// The read model's entity type for <paramref name="entity"/> — EF's own metadata, and deliberately so.
    /// It is the single authority for the two things this statement cannot compose without it: a masked
    /// column's <em>store type</em>, which only the provider's type mapping knows (this port's first revision
    /// derived one from <c>FieldSchema</c> instead and disagreed with the real columns for every faceted
    /// type), and which column the row <em>key</em> is, which a field mask must never hide. A store-type
    /// resolver callback would answer the first question and not the second, and would reintroduce exactly
    /// the second authority that was deleted.
    /// </param>
    internal ReadStatement Compose(
        EntitySchema entity,
        PolicyDecision decision,
        AlvoContext context,
        ReadStatementOptions options,
        IEntityType rows)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rows);

        var terms = new List<string>();
        var parameters = new Dictionary<string, BoundValue>(StringComparer.Ordinal);

        AddPredicate(terms, parameters, decision.Using, context, PolicyParameterPrefix.Using);
        AddPredicate(terms, parameters, decision.TenantScope, context, PolicyParameterPrefix.TenantScope);
        AddRowId(terms, parameters, entity, options.RowId);
        AddFilter(terms, parameters, entity, options.Filter);
        AddAnchor(terms, parameters, entity, options.Anchor);

        var sql = new StringBuilder("SELECT ")
            .Append(ReadProjection.Compose(entity, Mask(decision, options), _dialect, rows))
            .Append(" FROM ")
            .Append(_dialect.RenderTable(entity, options.LockFor))
            .Append(" WHERE ")
            .Append(string.Join(" AND ", terms.Select(term => $"({term})")))
            .Append(OrderByClause(entity, options))
            .Append(LimitClause(parameters, options))
            .Append(OffsetClause(parameters, options))
            .Append(LockClause(options))
            .ToString();

        return new ReadStatement(sql, parameters);
    }

    /// <inheritdoc cref="ReadStatementOptions.Unmasked"/>
    private static IReadOnlySet<string> Mask(PolicyDecision decision, ReadStatementOptions options) =>
        options.Unmasked ? _noMask : decision.HiddenFields;

    private static readonly IReadOnlySet<string> _noMask = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The ordering, when this read is one whose row order is observable — a caller sort, a limit that
    /// truncates, or a cursor whose boundary is only meaningful against a total order. A first page with
    /// none of those returns the whole visible set, where <see cref="AlvoQuery.Sort"/> documents the order as
    /// implementation-defined, so nothing is spent making it total.
    /// </summary>
    private string OrderByClause(EntitySchema entity, ReadStatementOptions options) =>
        RequiresTotalOrder(options)
            ? " ORDER BY " + SortSqlRenderer.Render(options.Sort, entity, _fields)
            : string.Empty;

    private static bool RequiresTotalOrder(ReadStatementOptions options) =>
        options.Sort.Count > 0 || options.Limit is not null || options.Offset is not null || options.Anchor is not null;

    /// <summary>
    /// <c>RowLimitClause</c> carries no separator of its own, like <c>RowLockClause</c>, so the separating
    /// space is inserted here. The row count is bound, never formatted: it is caller-supplied.
    /// </summary>
    /// <remarks>
    /// Renders even when <see cref="ReadStatementOptions.Limit"/> is <see langword="null"/> but
    /// <see cref="ReadStatementOptions.Offset"/> is not, binding <see cref="UnboundedRowCount"/> as the row
    /// count: SQLite's grammar makes <c>OFFSET</c> a sub-clause of <c>LIMIT</c> and rejects a bare
    /// <c>OFFSET</c> outright, so an offset with no caller-supplied limit still needs one rendered — a large
    /// value both shipped engines accept as an ordinary row count, standing in for "no bound" rather than a
    /// negative sentinel PostgreSQL's own <c>LIMIT</c> refuses.
    /// </remarks>
    private string LimitClause(Dictionary<string, BoundValue> parameters, ReadStatementOptions options)
    {
        var limit = options.Limit ?? (options.Offset is not null ? UnboundedRowCount : (int?)null);
        if (limit is not { } value)
        {
            return string.Empty;
        }

        parameters[PolicyParameterPrefix.RowLimit] = BoundValue.FromFramework(value);
        return " " + _dialect.RowLimitClause(_fields.RenderParameter(PolicyParameterPrefix.RowLimit));
    }

    /// <summary>
    /// The row count <see cref="LimitClause"/> binds when a caller asks for <see cref="ReadStatementOptions.Offset"/>
    /// with no explicit <see cref="ReadStatementOptions.Limit"/>. Large enough that no real page is ever
    /// bounded by it, and small enough that it binds through the same <see langword="int"/> column
    /// <see cref="AlvoQuery.Limit"/> itself does.
    /// </summary>
    private const int UnboundedRowCount = int.MaxValue;

    /// <summary>
    /// <c>RowOffsetClause</c> carries no separator of its own, like <c>RowLimitClause</c>. Composed
    /// immediately after it, matching the one order both shipped engines' native grammar accepts
    /// (<c>LIMIT n OFFSET m</c>) — see <see cref="IAlvoSqlDialect.RowOffsetClause"/>'s remarks for why this
    /// is a fixed order rather than one a dialect can choose.
    /// </summary>
    private string OffsetClause(Dictionary<string, BoundValue> parameters, ReadStatementOptions options)
    {
        if (options.Offset is not { } offset)
        {
            return string.Empty;
        }

        parameters[PolicyParameterPrefix.RowOffset] = BoundValue.FromFramework(offset);
        return " " + _dialect.RowOffsetClause(_fields.RenderParameter(PolicyParameterPrefix.RowOffset));
    }

    /// <summary>
    /// <c>RowLockClause</c> carries no separator of its own (see <see cref="IAlvoSqlDialect.RowLockClause"/>),
    /// so the separating space is inserted here and only when there is a clause to separate. An empty answer
    /// is not "no lock was asked for": the same <see cref="ReadStatementOptions.LockFor"/> also reaches
    /// <see cref="IAlvoSqlDialect.RenderTable"/>, which is where a dialect whose grammar is a table hint takes
    /// the lock instead.
    /// </summary>
    private string LockClause(ReadStatementOptions options) =>
        options.LockFor is { } mutation && _dialect.RowLockClause(mutation) is { Length: > 0 } clause
            ? " " + clause
            : string.Empty;

    /// <summary>
    /// A <see langword="null"/> predicate contributes the dialect's constant-true predicate rather than
    /// nothing: <c>create</c> carries no <c>USING</c> and a global entity carries no tenant scope, and a
    /// <c>WHERE</c> clause with no term at all is a syntax error. A constant true is safe here precisely
    /// because <see cref="IPolicyEngine"/> already denied every operation that has no predicate for a
    /// reason.
    /// </summary>
    private void AddPredicate(
        List<string> terms, Dictionary<string, BoundValue> parameters,
        CompiledExpression? expression, AlvoContext context, string prefix)
    {
        if (expression is null)
        {
            terms.Add(_fields.RenderBooleanPredicate(true));
            return;
        }

        var predicate = _predicates.Render(expression, context, _fields, prefix);
        terms.Add(predicate.Sql);
        Collect(parameters, PolicyValues(predicate.Parameters));
    }

    /// <summary>
    /// A rendered <c>SqlPredicate</c>'s bag records names and values only — it carries no field — so its values
    /// are tagged as the policy predicate's rather than as any column's. See
    /// <see cref="BoundValue.FromPolicyPredicate"/> for why the CEL type checker makes that sufficient.
    /// </summary>
    private static Dictionary<string, BoundValue> PolicyValues(IReadOnlyDictionary<string, object?> rendered) =>
        rendered.ToDictionary(pair => pair.Key, pair => BoundValue.FromPolicyPredicate(pair.Value), StringComparer.Ordinal);

    /// <summary>
    /// The row-id term, when the read names one. The guard is a plain <see langword="null"/> test rather
    /// than the negated declaration pattern it used to be, because a pattern variable assigned only on the
    /// guard's failing branch makes every mutation of this method a compile error — so Stryker's Safe Mode
    /// removed all of them and the mutation gate reported nothing at all about the term that carries a row
    /// identity into a policy-filtered statement. See <c>docs/architecture/data-path.md</c>.
    /// </summary>
    private void AddRowId(List<string> terms, Dictionary<string, BoundValue> parameters, EntitySchema entity, Guid? rowId)
    {
        if (rowId is null)
        {
            return;
        }

        terms.Add(
            $"{_fields.RenderField(entity, AlvoDataContext.IdColumn)} = {_fields.RenderParameter(PolicyParameterPrefix.RowId)}");
        parameters[PolicyParameterPrefix.RowId] = BoundValue.ForColumn(AlvoDataContext.IdColumn, rowId.Value);
    }

    private void AddFilter(
        List<string> terms, Dictionary<string, BoundValue> parameters, EntitySchema entity, AlvoFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        var rendered = FilterSqlRenderer.Render(filter, entity, _fields, PolicyParameterPrefix.Filter);
        terms.Add(rendered.Sql);
        Collect(parameters, rendered.Parameters);
    }

    private void AddAnchor(
        List<string> terms, Dictionary<string, BoundValue> parameters, EntitySchema entity, KeysetAnchor? anchor)
    {
        if (anchor is null)
        {
            return;
        }

        var rendered = KeysetSqlRenderer.Render(anchor, entity, _fields, PolicyParameterPrefix.Keyset);
        terms.Add(rendered.Sql);
        Collect(parameters, rendered.Parameters);
    }

    /// <summary>
    /// Copies a rendered fragment's bound values into the statement's bag, refusing a name two fragments both
    /// claim. Every fragment is rendered with its own reserved prefix, so a collision is a bug in the
    /// prefixes rather than something to resolve last-writer-wins — and last-writer-wins is exactly how one
    /// predicate silently changes what another one means.
    /// <para>
    /// Unreachable today, and deliberately kept: every fragment is rendered with a name from
    /// <see cref="PolicyParameterPrefix"/> and those are pairwise disjoint by test, so this can only fire if
    /// a later fragment is added without its own reserved prefix — the one mistake that would otherwise
    /// produce wrong rows rather than an error.
    /// </para>
    /// </summary>
    private static void Collect(Dictionary<string, BoundValue> parameters, IReadOnlyDictionary<string, BoundValue> rendered)
    {
        foreach (var (name, value) in rendered)
        {
            if (!parameters.TryAdd(name, value))
            {
                throw new InvalidOperationException(
                    $"Parameter '{name}' is claimed by two fragments of one statement; the reserved prefixes "
                    + "must keep every fragment's names disjoint.");
            }
        }
    }
}
