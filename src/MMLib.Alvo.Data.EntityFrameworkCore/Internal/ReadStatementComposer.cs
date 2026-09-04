using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Collections.Frozen;
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
        /// The declared fields the caller's projection excluded, rendered as projected <c>NULL</c>s instead
        /// of being read. Empty for every read but a projected page.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Separate from the decision's field mask, deliberately</b> — see <see cref="ReadProjection"/>
        /// for why a caller preference and a security control must not share one parameter.
        /// </para>
        /// <para>
        /// <b>Empty on every path but the page.</b> A pre-image, a policy root and a single-row read each
        /// build their own options and take this default. <see cref="ComposeCount"/> is the exception worth
        /// knowing about: it is handed the page's own record and simply <em>ignores</em> this, exactly as it
        /// ignores <see cref="Anchor"/>, <see cref="Sort"/>, <see cref="Limit"/> and <see cref="Offset"/> —
        /// it composes no projection at all, so there is nothing here for it to apply. Narrowing the record
        /// it is handed to "protect" it would undo the drift guard that convention exists for.
        /// </para>
        /// </remarks>
        internal IReadOnlySet<string> Unselected { get; init; } = FrozenSet<string>.Empty;

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

        var (terms, parameters) = Terms(entity, decision, context, options);

        var sql = new StringBuilder("SELECT ")
            .Append(ReadProjection.Compose(entity, Mask(decision, options), options.Unselected, _dialect, rows))
            .Append(" FROM ")
            .Append(_dialect.RenderTable(entity, options.LockFor))
            .Append(" WHERE ")
            .Append(Where(terms))
            .Append(OrderByClause(entity, options))
            .Append(WindowClause(parameters, options))
            .Append(LockClause(options))
            .ToString();

        return new ReadStatement(sql, parameters);
    }

    /// <summary>
    /// Composes the <c>COUNT</c> that answers <see cref="AlvoQuery.IncludeTotalCount"/> — <b>how many rows
    /// the caller can see</b>, over the same <c>WHERE</c> terms as the page and in the same order, so the
    /// number and the rows are filtered by one predicate rather than by two that have to be kept in step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It differs from <see cref="Compose"/> in exactly three ways, and each of them is the point. There is
    /// no projection — <c>COUNT(*)</c> reads no column, so a masked field has nothing to leak through and
    /// <see cref="ReadProjection"/> is never reached. There is no <c>ORDER BY</c> and no row window: a count
    /// has no order, and it is a count of the <em>set</em>, not of the page that is a window onto it. And the
    /// <b>keyset anchor is not composed</b>: the anchor narrows the statement to the rows after the cursor,
    /// which is precisely what a total must not be.
    /// </para>
    /// <para>
    /// <b>Why the anchor's absence is a decision rather than an omission.</b> The natural one-statement form
    /// — PostgREST's <c>COUNT(*) OVER ()</c> beside the rows — evaluates after <c>WHERE</c>, and Alvo's
    /// <c>WHERE</c> carries the cursor boundary, so it would count the tail rather than the set on every page
    /// but the first. Two statements is the price of the count meaning the same thing on page one and page
    /// nine; the cost is that a write interleaving them can make the number disagree with the rows by one.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity being counted, as the applied schema declares it.</param>
    /// <param name="decision">The verdict <see cref="IPolicyEngine"/> returned for this caller.</param>
    /// <param name="context">The caller the predicates' context values are resolved against.</param>
    /// <param name="options">
    /// The same options the page was composed from. <see cref="ReadStatementOptions.Anchor"/>,
    /// <see cref="ReadStatementOptions.Sort"/>, <see cref="ReadStatementOptions.Limit"/>,
    /// <see cref="ReadStatementOptions.Offset"/> and <see cref="ReadStatementOptions.Unselected"/> are
    /// deliberately ignored; the caller passes the whole record rather than a narrowed copy so that a term
    /// added to the read cannot be silently missed here. <c>Unselected</c> is ignored for a stronger reason
    /// than the rest: this method composes no projection at all, so there is nothing for it to apply.
    /// </param>
    internal ReadStatement ComposeCount(
        EntitySchema entity, PolicyDecision decision, AlvoContext context, ReadStatementOptions options)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var (terms, parameters) = Terms(entity, decision, context, options with { Anchor = null });
        var sql = $"SELECT COUNT(*) FROM {_dialect.RenderTable(entity, lockedPreImageFor: null)} WHERE {Where(terms)}";

        return new ReadStatement(sql, parameters);
    }

    /// <summary>
    /// The <c>WHERE</c> terms every read of this entity carries, in one place so the page and its count
    /// cannot come to disagree about what "the visible set" is. The caller's own terms are only ever
    /// <c>AND</c>-ed onto a fully parenthesised policy predicate, so they can only narrow.
    /// </summary>
    private (List<string> Terms, Dictionary<string, BoundValue> Parameters) Terms(
        EntitySchema entity, PolicyDecision decision, AlvoContext context, ReadStatementOptions options)
    {
        var terms = new List<string>();
        var parameters = new Dictionary<string, BoundValue>(StringComparer.Ordinal);

        AddPredicate(terms, parameters, decision.Using, context, PolicyParameterPrefix.Using);
        AddPredicate(terms, parameters, decision.TenantScope, context, PolicyParameterPrefix.TenantScope);
        AddRowId(terms, parameters, entity, options.RowId);
        AddFilter(terms, parameters, entity, options.Filter);
        AddAnchor(terms, parameters, entity, options.Anchor);

        return (terms, parameters);
    }

    private static string Where(List<string> terms) => string.Join(" AND ", terms.Select(term => $"({term})"));

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
    /// <c>RowWindowClause</c> carries no separator of its own, like <c>RowLockClause</c>, so the separating
    /// space is inserted here. Both bound values are caller-supplied and therefore never formatted into the
    /// text.
    /// </summary>
    /// <remarks>
    /// Renders whenever either <see cref="ReadStatementOptions.Limit"/> or
    /// <see cref="ReadStatementOptions.Offset"/> is set — never only when both are, because SQLite's grammar
    /// makes <c>OFFSET</c> a sub-clause of <c>LIMIT</c> and rejects a bare one outright. An offset with no
    /// caller-supplied limit therefore still binds a row count, <see cref="UnboundedRowCount"/>: a large
    /// value both shipped engines accept as an ordinary <c>LIMIT</c>, standing in for "no bound" rather than
    /// a negative sentinel PostgreSQL's own <c>LIMIT</c> refuses. One call renders the whole window — see
    /// <see cref="IAlvoSqlDialect.RowWindowClause"/>'s remarks for why this is one member rather than two:
    /// splitting it is what let <c>TSqlSqlDialect</c> answer each half correctly and the pair wrongly.
    /// </remarks>
    private string WindowClause(Dictionary<string, BoundValue> parameters, ReadStatementOptions options)
    {
        var limit = options.Limit ?? (options.Offset is not null ? UnboundedRowCount : (int?)null);
        if (limit is not { } value)
        {
            return string.Empty;
        }

        parameters[PolicyParameterPrefix.RowLimit] = BoundValue.FromFramework(value);

        string? offsetMarker = null;
        if (options.Offset is { } offset)
        {
            parameters[PolicyParameterPrefix.RowOffset] = BoundValue.FromFramework(offset);
            offsetMarker = _fields.RenderParameter(PolicyParameterPrefix.RowOffset);
        }

        return " " + _dialect.RowWindowClause(_fields.RenderParameter(PolicyParameterPrefix.RowLimit), offsetMarker);
    }

    /// <summary>
    /// The row count <see cref="WindowClause"/> binds when a caller asks for <see cref="ReadStatementOptions.Offset"/>
    /// with no explicit <see cref="ReadStatementOptions.Limit"/>. Large enough that no real page is ever
    /// bounded by it, and small enough that it binds through the same <see langword="int"/> column
    /// <see cref="AlvoQuery.Limit"/> itself does.
    /// </summary>
    private const int UnboundedRowCount = int.MaxValue;

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
