using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>One composed read statement and the values its named parameters bind.</summary>
/// <param name="Sql">The statement text.</param>
/// <param name="Parameters">The values <paramref name="Sql"/> references by name.</param>
internal sealed record ReadStatement(string Sql, IReadOnlyDictionary<string, object?> Parameters);

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
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

        AddPredicate(terms, parameters, decision.Using, context, PolicyParameterPrefix.Using);
        AddPredicate(terms, parameters, decision.TenantScope, context, PolicyParameterPrefix.TenantScope);
        AddRowId(terms, parameters, entity, options.RowId);
        AddFilter(terms, parameters, entity, options.Filter);
        AddAnchor(terms, parameters, entity, options.Anchor);

        var sql = new StringBuilder("SELECT ")
            .Append(ReadProjection.Compose(entity, decision.HiddenFields, _dialect, rows))
            .Append(" FROM ")
            .Append(_dialect.RenderTable(entity))
            .Append(" WHERE ")
            .Append(string.Join(" AND ", terms.Select(term => $"({term})")))
            .Append(LockClause(options))
            .ToString();

        return new ReadStatement(sql, parameters);
    }

    /// <summary>
    /// <c>RowLockClause</c> carries no separator of its own (see <see cref="IAlvoSqlDialect.RowLockClause"/>),
    /// so the separating space is inserted here and only when there is a clause to separate.
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
        List<string> terms, Dictionary<string, object?> parameters,
        CompiledExpression? expression, AlvoContext context, string prefix)
    {
        if (expression is null)
        {
            terms.Add(_fields.RenderBooleanPredicate(true));
            return;
        }

        var predicate = _predicates.Render(expression, context, _fields, prefix);
        terms.Add(predicate.Sql);
        Collect(parameters, predicate.Parameters);
    }

    private void AddRowId(List<string> terms, Dictionary<string, object?> parameters, EntitySchema entity, Guid? rowId)
    {
        if (rowId is not { } id)
        {
            return;
        }

        terms.Add(
            $"{_fields.RenderField(entity, AlvoDataContext.IdColumn)} = {_fields.RenderParameter(PolicyParameterPrefix.RowId)}");
        parameters[PolicyParameterPrefix.RowId] = id;
    }

    private void AddFilter(
        List<string> terms, Dictionary<string, object?> parameters, EntitySchema entity, AlvoFilter? filter)
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
        List<string> terms, Dictionary<string, object?> parameters, EntitySchema entity, KeysetAnchor? anchor)
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
    private static void Collect(Dictionary<string, object?> parameters, IReadOnlyDictionary<string, object?> rendered)
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
