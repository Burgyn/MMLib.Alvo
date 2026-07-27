using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
    private readonly TimeProvider _time;

    internal EfAlvoData(
        IPolicyEngine policy,
        IPredicateEvaluator evaluator,
        IPredicateRenderer predicates,
        IFieldSqlRenderer fields,
        IAlvoSqlDialect dialect,
        AlvoDataContextFactory contexts,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(time);
        _policy = policy;
        _evaluator = evaluator;
        _statements = new ReadStatementComposer(predicates, fields, dialect);
        _contexts = contexts;
        _time = time;
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
        EnsureSortKeysCanBePaged(query, entity);

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
    public async Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Create, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        WritePayloadGuard.EnsureWritable(values, schema, decision, isUpdate: false);

        var candidate = Candidate(db.Rows(entity).EntityType, Stamped(schema, values, context, isUpdate: false));
        EnsureWriteAllowed(decision, RecordMaterializer.ToRecord(candidate, _noMask), previous: null, context);

        db.Rows(entity).Add(candidate);
        await db.SaveChangesAsync(cancellationToken);

        return RecordMaterializer.ToRecord(candidate, decision.HiddenFields);
    }

    /// <summary>
    /// The candidate row: the payload as <see cref="WritePropertyBag"/> prepares it, plus the id this provider
    /// assigns.
    /// </summary>
    private static Dictionary<string, object> Candidate(
        IEntityType rows, IReadOnlyDictionary<string, object?> values)
    {
        var candidate = WritePropertyBag.For(rows, values);
        candidate[AlvoDataContext.IdColumn] = Guid.NewGuid();

        return candidate;
    }

    /// <summary>
    /// Evaluates <c>WITH CHECK</c> and the synthesized tenant scope over the <b>complete post-image</b>,
    /// never over the payload alone — a field the caller did not mention has to read as its stored value,
    /// or an update touching one unrelated field would be denied by its own ownership rule. Evaluating the
    /// tenant scope here, and not only on the read side, is what stops a caller placing or moving a row
    /// into another tenant.
    /// </summary>
    private void EnsureWriteAllowed(
        PolicyDecision decision, AlvoRecord postImage, AlvoRecord? previous, AlvoContext context)
    {
        var passesCheck = decision.WithCheck is null
            || _evaluator.Evaluate(decision.WithCheck, postImage, previous, context);
        var passesTenantScope = decision.TenantScope is null
            || _evaluator.Evaluate(decision.TenantScope, postImage, previous, context);

        if (!passesCheck || !passesTenantScope)
        {
            throw new AlvoAuthorizationException("The write was rejected by policy.");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Merge-then-check inside one transaction, never write-then-rollback: the pre-image is read under
    /// <c>USING</c> with the driver's row lock, the patch is merged over it, <c>WITH CHECK</c> and the tenant
    /// scope are evaluated over that complete post-image, and only then does the <c>ExecuteUpdate</c> run —
    /// still constrained by <c>USING</c>, so the row cannot have been taken away in between. A rollback is
    /// not control flow here, and the verdict stays in the engine-agnostic core.
    /// </remarks>
    public async Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Update, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        WritePayloadGuard.EnsureWritable(values, schema, decision, isUpdate: true);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var postImage = await WriteAsync(
            db, schema, decision, context, id, Stamped(schema, values, context, isUpdate: true), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RecordMaterializer.ToRecord(postImage, decision.HiddenFields);
    }

    /// <summary>
    /// The payload the write actually carries: the caller's, plus the audit columns this framework owns.
    /// </summary>
    /// <remarks>
    /// Stamped <em>after</em> <see cref="WritePayloadGuard.EnsureWritable"/> and <em>before</em>
    /// <see cref="EnsureWriteAllowed"/>, which is the only order that is both safe and useful: the guard
    /// must judge the caller's own keys, and the check predicate must see the values that will be stored —
    /// so a create rule reading <c>created_by == @user.id</c> is satisfied by the stamp rather than by
    /// something the caller claimed.
    /// </remarks>
    private IReadOnlyDictionary<string, object?> Stamped(
        EntitySchema schema, IReadOnlyDictionary<string, object?> values, AlvoContext context, bool isUpdate) =>
        AlvoAuditStamp.Applied(schema, values, context, _time, isUpdate);

    /// <inheritdoc/>
    public async Task DeleteAsync(
        string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Delete, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);

        var affected = await RowOf(PolicyRoot(db, schema, decision, context), id)
            .ExecuteDeleteAsync(cancellationToken);
        if (affected == 0)
        {
            throw new AlvoRecordNotFoundException();
        }
    }

    /// <summary>
    /// The body of one update, inside the caller's transaction: the locked unmasked pre-image, the merged
    /// post-image's verdict, the policy-carrying write, and the re-read that produces what is returned.
    /// </summary>
    private async Task<Dictionary<string, object>> WriteAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Guid id, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken)
    {
        var stored = await SingleAsync(
            db, schema, decision, context, id, PreImageMutation.Update, cancellationToken, unmasked: true)
            ?? throw new AlvoRecordNotFoundException();

        var preImage = RecordMaterializer.ToRecord(stored, _noMask);
        EnsureWriteAllowed(decision, Merge(preImage, values), preImage, context);

        if (await AffectedAsync(db, schema, decision, context, id, values, cancellationToken) == 0)
        {
            throw new AlvoRecordNotFoundException();
        }

        return await SingleAsync(db, schema, decision, context, id, lockFor: null, cancellationToken)
            ?? throw new AlvoRecordNotFoundException();
    }

    private async Task<int> AffectedAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Guid id, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken)
        => await RowOf(PolicyRoot(db, schema, decision, context), id)
            .ExecuteUpdateAsync(UpdateSetterFactory.For(schema, values), cancellationToken);

    /// <summary>
    /// The queryable a write is composed over: a <c>FromSql</c> root whose <c>WHERE</c> already carries the
    /// <c>USING</c> predicate and the tenant scope, so the emitted <c>UPDATE</c>/<c>DELETE</c> constrains
    /// the row through a subquery the caller cannot influence and <c>rows affected == 0</c> means "no such
    /// visible row".
    /// </summary>
    private IQueryable<Dictionary<string, object>> PolicyRoot(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context)
    {
        var statement = _statements.Compose(
            schema, decision, context, new ReadStatementComposer.ReadStatementOptions(),
            db.Rows(schema.Name).EntityType);

        return Materialize(db, schema, statement);
    }

    /// <summary>
    /// Narrows the policy root to one row id. Composed in LINQ rather than into the raw text: that is the
    /// exact shape proved to emit one statement on both engines
    /// (<c>UPDATE … FROM (SELECT id FROM (&lt;root&gt;) WHERE id = @p) …</c>), and the comparison is written
    /// against <see cref="Guid"/>? because every read-model property is nullable.
    /// </summary>
    private static IQueryable<Dictionary<string, object>> RowOf(
        IQueryable<Dictionary<string, object>> root, Guid id) =>
        root.Where(row => EF.Property<Guid?>(row, AlvoDataContext.IdColumn) == id);

    private static AlvoRecord Merge(AlvoRecord stored, IReadOnlyDictionary<string, object?> values)
    {
        var merged = stored;
        foreach (var (field, value) in values)
        {
            merged = merged.With(field, value);
        }

        return merged;
    }

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
    /// Refuses a <b>paged</b> read whose sort key names a nullable field. The keyset boundary is a chain of
    /// comparisons with no <c>IS NULL</c> arm, so a <c>NULL</c> on either side makes the term <c>NULL</c> and a
    /// <c>WHERE</c> treats that as false: the page would stop early and silently, losing every null-keyed row
    /// under <c>nullslast</c> and every row but the first under <c>nullsfirst</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The design's ruling is that a nullable sort column must declare its null placement <b>or be
    /// rejected</b>; the third option — accept the query and lose rows — is what this refuses. It is the
    /// port's malformed-query channel rather than an authorization refusal, because the field is one the
    /// caller can read and nothing is being hidden; a structured error is also what a request layer above this
    /// port can turn into a 422 with a fix suggestion.
    /// </para>
    /// <para>
    /// Scoped to a paged read on purpose. An unpaged sorted read has no boundary, so its ordering over nulls is
    /// already correct and refusing it would break whole-set reads for no gain. Making such a page work needs an
    /// <c>IS NULL</c>-aware boundary whose predicate form depends on the anchor's own null-ness — a shape change
    /// to <see cref="KeysetSqlRenderer"/> that must stay in lockstep with <see cref="SortSqlRenderer"/>'s rank
    /// expression — and that belongs with the work that owns the paging surface.
    /// </para>
    /// </remarks>
    private static void EnsureSortKeysCanBePaged(AlvoQuery query, EntitySchema? entity)
    {
        if (entity is null || (query.Limit is null && query.After is null))
        {
            return;
        }

        foreach (var key in query.Sort.Where(key => QueryFieldGuard.DeclaredField(entity, key.Field).Nullable))
        {
            throw new ArgumentException(
                $"Sorting a paged read by '{key.Field}' is not supported, because that field is nullable and a "
                + "keyset cursor cannot express where its null values sort. Page by a required field, or read the "
                + "whole set without a limit or a cursor.",
                nameof(query));
        }
    }

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
        AlvoDataContext db, EntitySchema entity, ReadStatement statement)
    {
        var rows = db.Rows(entity.Name);
        return rows.FromSqlRaw(
            statement.Sql, new PredicateParameterBinder(db).Bind(rows.EntityType, statement.Parameters));
    }

    /// <summary>
    /// The empty mask: a row a policy decision is <em>reached over</em> is never masked, only a row this data
    /// path <em>returns</em> is. A masked field read as <see langword="null"/> would silently change what a
    /// rule referencing it decides.
    /// </summary>
    private static readonly IReadOnlySet<string> _noMask = new HashSet<string>(StringComparer.Ordinal);

    /// <inheritdoc cref="AlvoDataContext.UnmappedEntityMessage"/>
    private const string UnknownEntityMessage = AlvoDataContext.UnmappedEntityMessage;
}
