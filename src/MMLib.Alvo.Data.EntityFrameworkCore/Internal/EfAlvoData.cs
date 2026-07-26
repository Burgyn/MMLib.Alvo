using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The EF Core implementation of <see cref="IAlvoData"/>: policy is enforced <em>inside</em> this type,
/// as a predicate the database evaluates, never as a filter this process applies to rows it already
/// fetched.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="DbContext"/> this class creates never escapes it, and that is a security boundary
/// rather than encapsulation taste: a tracked, mutated property bag saved through the change tracker
/// emits <c>UPDATE … WHERE id = @p</c> with no policy predicate at all — the shortest and most idiomatic
/// EF code available, which compiles, passes a naive test, and bypasses authorization completely. Writes
/// here therefore run as <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> over the same <c>FromSql</c> root that
/// carries the <c>USING</c> predicate, and queries never track.
/// </para>
/// <para>
/// The order of the checks is the contract. Policy first, so a denied operation reveals nothing about the
/// entity's shape. Then the filter depth cap, because every backend walks a filter recursively. Then the
/// filter and sort field names, because those are the only caller-supplied strings that reach SQL as
/// identifiers. Only then is a statement composed.
/// </para>
/// <para>
/// Nothing is composed over the read statement in LINQ — not the ordering, not the limit — so EF has no
/// reason to wrap it in a derived table and the text Alvo composed is the text the engine runs. That is
/// what makes a page's order and a page's boundary provably the same sequence.
/// </para>
/// </remarks>
internal sealed class EfAlvoData : IAlvoData
{
    private readonly IPolicyEngine _policy;
    private readonly IPredicateEvaluator _evaluator;
    private readonly ReadStatementComposer _statements;
    private readonly AlvoDataContextFactory _contexts;

    internal EfAlvoData(
        IPolicyEngine policy,
        IPredicateEvaluator evaluator,
        IPredicateRenderer predicates,
        IFieldSqlRenderer fields,
        IAlvoSqlDialect dialect,
        AlvoDataContextFactory contexts)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(contexts);
        _policy = policy;
        _evaluator = evaluator;
        _statements = new ReadStatementComposer(predicates, fields, dialect);
        _contexts = contexts;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AlvoRecord>> QueryAsync(
        AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(query.Entity, DataOperation.List, context);
        AlvoFilter.EnsureWithinDepthLimit(query.Filter);
        EnsureLimitIsSane(query.Limit);

        using var db = _contexts.Create();
        var entity = Entity(db, query.Entity);
        QueryFieldGuard.EnsureAvailable(QueryFields(query), entity, decision.HiddenFields);

        var anchor = await AnchorAsync(db, entity, decision, context, query, cancellationToken);
        if (query.After is not null && anchor is null)
        {
            return [];
        }

        var rows = await PageAsync(db, entity, decision, context, query, anchor, cancellationToken);
        return [.. rows.Select(row => RecordMaterializer.ToRecord(row, decision.HiddenFields))];
    }

    /// <inheritdoc/>
    public async Task<AlvoRecord?> GetAsync(
        string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Get, context);

        using var db = _contexts.Create();
        var row = await SingleAsync(db, Entity(db, entity), decision, context, id, lockFor: null, cancellationToken);
        return row is null ? null : RecordMaterializer.ToRecord(row, decision.HiddenFields);
    }

    /// <inheritdoc/>
    public Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context,
        CancellationToken cancellationToken = default) => throw new NotSupportedException(WritePathPending);

    /// <inheritdoc/>
    public Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context,
        CancellationToken cancellationToken = default) => throw new NotSupportedException(WritePathPending);

    /// <inheritdoc/>
    public Task DeleteAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(WritePathPending);

    private const string WritePathPending = "The write path is not implemented yet.";

    private PolicyDecision Resolve(string entity, DataOperation operation, AlvoContext context)
    {
        var decision = _policy.Resolve(entity, operation, context);
        return decision.IsDenied
            ? throw new AlvoAuthorizationException(decision.DenyReason ?? UnknownEntityMessage)
            : decision;
    }

    /// <summary>
    /// A negative page size is a malformed query, not an authorization question: it discloses nothing, and a
    /// caller needs to know their query shape was refused rather than their permissions — the same reasoning
    /// (and the same exception family) as <see cref="AlvoFilter.EnsureWithinDepthLimit"/>. It is refused here
    /// rather than passed on because the two engines disagree about it: PostgreSQL raises, SQLite reads
    /// <c>LIMIT -1</c> as "no limit at all" and silently returns the whole page.
    /// </summary>
    private static void EnsureLimitIsSane(int? limit) =>
        ArgumentOutOfRangeException.ThrowIfNegative(limit ?? 0, nameof(AlvoQuery.Limit));

    /// <summary>
    /// Resolves the entity from the <b>applied schema this context's model was built from</b>. A dynamic
    /// entity resolves to <see langword="null"/> here, so it is refused exactly like an unknown one — the
    /// dynamic driver is a different <see cref="IAlvoSqlDialect"/>, registered later, not a branch in this
    /// class.
    /// </summary>
    private static EntitySchema? Entity(AlvoDataContext db, string entity) => db.AppliedSchema.Entities
        .FirstOrDefault(candidate =>
            string.Equals(candidate.Name, entity, StringComparison.Ordinal)
            && candidate.Storage == EntityStorage.Physical);

    private static IEnumerable<string> QueryFields(AlvoQuery query) =>
        AlvoFilter.ReferencedFields(query.Filter).Concat(query.Sort.Select(sort => sort.Field));

    /// <summary>
    /// Reads the one row <paramref name="id"/> names, still under the policy predicate, so a row the caller
    /// cannot see is indistinguishable from one that does not exist. <c>unmasked</c> is
    /// <see langword="true"/> only for a pre-image a <c>WITH CHECK</c> verdict is reached over, where the
    /// check has to see a masked field's real stored value.
    /// </summary>
    private async Task<Dictionary<string, object>?> SingleAsync(
        AlvoDataContext db, EntitySchema? entity, PolicyDecision decision, AlvoContext context,
        Guid id, PreImageMutation? lockFor, CancellationToken cancellationToken, bool unmasked = false)
    {
        var schema = entity ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        var statement = _statements.Compose(
            schema,
            decision,
            context,
            new ReadStatementComposer.ReadStatementOptions { RowId = id, LockFor = lockFor, Unmasked = unmasked },
            db.Rows(schema.Name).EntityType);

        var rows = await Materialize(db, schema, statement).ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Re-reads the cursor's anchor row <b>under the same decision as the page</b> and rebuilds the sort-key
    /// values the boundary predicate compares against. A cursor therefore carries no data of its own: a
    /// stale, forged or cross-tenant one finds no anchor, and the caller gets an empty page rather than an
    /// answer about a row they cannot see.
    /// </summary>
    private async Task<KeysetAnchor?> AnchorAsync(
        AlvoDataContext db, EntitySchema? entity, PolicyDecision decision, AlvoContext context,
        AlvoQuery query, CancellationToken cancellationToken)
    {
        if (query.After is null || !KeysetCursor.TryDecode(query.After, out var anchorId))
        {
            return null;
        }

        var row = await SingleAsync(db, entity, decision, context, anchorId, lockFor: null, cancellationToken);
        return row is null
            ? null
            : new KeysetAnchor(query.Sort, [.. query.Sort.Select(key => row.GetValueOrDefault(key.Field))], anchorId);
    }

    /// <summary>
    /// The page itself: one statement carrying the policy predicate, the tenant scope, the caller's filter,
    /// the cursor boundary, the ordering and the limit — so the limit truncates the ordered, policy-filtered
    /// set and can never truncate the table before the predicate has seen it.
    /// </summary>
    private async Task<List<Dictionary<string, object>>> PageAsync(
        AlvoDataContext db, EntitySchema? entity, PolicyDecision decision, AlvoContext context,
        AlvoQuery query, KeysetAnchor? anchor, CancellationToken cancellationToken)
    {
        var schema = entity ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        var statement = _statements.Compose(
            schema,
            decision,
            context,
            new ReadStatementComposer.ReadStatementOptions
            {
                Filter = query.Filter,
                Anchor = anchor,
                Sort = query.Sort,
                Limit = query.Limit,
            },
            db.Rows(schema.Name).EntityType);

        return await Materialize(db, schema, statement).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Runs one composed statement as a <c>FromSql</c> root with <b>nothing composed over it</b>, so EF
    /// executes the text verbatim instead of pushing it into a derived table whose row order is not
    /// guaranteed to survive.
    /// </summary>
    private static IQueryable<Dictionary<string, object>> Materialize(
        AlvoDataContext db, EntitySchema entity, ReadStatement statement) => db.Rows(entity.Name)
        .FromSqlRaw(statement.Sql, new PredicateParameterBinder(db).Bind(statement.Parameters));

    /// <inheritdoc cref="AlvoDataContext.UnmappedEntityMessage"/>
    private const string UnknownEntityMessage = AlvoDataContext.UnmappedEntityMessage;
}
