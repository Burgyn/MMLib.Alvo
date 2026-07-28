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
    public async Task<AlvoPage> QueryAsync(
        AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(query.Entity, DataOperation.List, context);
        AlvoFilter.EnsureWithinLimits(query.Filter);
        AlvoQuery.EnsurePagingWindowIsSane(query);

        using var db = _contexts.Create();
        var entity = Entity(db, query.Entity);
        QueryFieldGuard.EnsureAvailable(QueryFields(query), entity, decision.HiddenFields);
        AlvoQuery.EnsureSortKeysCanBePaged(query, entity);

        var anchor = await AnchorAsync(db, entity, decision, context, query, cancellationToken);
        if (query.After is not null && anchor is null)
        {
            return AlvoPage.Empty;
        }

        var fetched = await PageAsync(db, entity, decision, context, query, anchor, cancellationToken);
        var (kept, nextCursor) = Paginated(fetched, query.Limit);
        return new AlvoPage
        {
            Items = [.. kept.Select(row => RecordMaterializer.ToRecord(row, decision.HiddenFields))],
            NextCursor = nextCursor,
        };
    }

    /// <summary>
    /// Splits an over-fetched row set back down to the page <paramref name="limit"/> asked for, and derives
    /// <see cref="AlvoPage.NextCursor"/> from whether the extra row actually came back.
    /// </summary>
    /// <remarks>
    /// Never derived from <c>Items.Count == limit</c>: that would mint a cursor for a page that happened to
    /// return exactly <paramref name="limit"/> rows because the visible set ended there too, and the
    /// client's next request would come back empty — a bug that only shows when the row count is a multiple
    /// of the page size. <see cref="PageAsync"/> over-fetches by one row precisely so this can tell "more
    /// rows exist" from "the set ended here" without a second round trip.
    /// </remarks>
    /// <param name="fetched">The rows <see cref="PageAsync"/> returned, over-fetched by one when <paramref name="limit"/> is set.</param>
    /// <param name="limit">The caller's own page size, or <see langword="null"/> for the whole visible set.</param>
    private static (List<Dictionary<string, object>> Kept, string? NextCursor) Paginated(
        List<Dictionary<string, object>> fetched, int? limit)
    {
        if (limit is not { } value || fetched.Count <= value)
        {
            return (fetched, null);
        }

        var kept = fetched.GetRange(0, value);
        var lastId = (Guid)kept[^1][AlvoDataContext.IdColumn];
        return (kept, KeysetCursor.Encode(lastId));
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

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stored = await InsertAsync(db, schema, decision, context, candidate, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RecordMaterializer.ToRecord(stored, decision.HiddenFields);
    }

    /// <summary>
    /// Inserts the candidate and <b>returns the row the database now holds</b>, re-read inside the same
    /// transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returning the candidate bag instead — the caller's own payload plus the generated id — makes the create
    /// response a different thing from the update response, which re-reads. Every database default is missing
    /// from it, a 201 has no <c>ETag</c> source, PR6's <c>computed</c> column has no value at all until the row
    /// exists, and the caller cannot see the audit values the framework just assigned. One re-read closes all
    /// four, and the transaction is what makes it the row this insert wrote rather than whatever a concurrent
    /// writer left behind.
    /// </para>
    /// <para>
    /// The re-read goes through the policy root like every other read here. <c>create</c> carries no
    /// <c>USING</c> predicate — there is no stored row to filter when the decision is made — so the root
    /// narrows to this one id under the synthesized tenant scope, which the candidate's post-image has already
    /// been checked against. It cannot come back empty for a row the caller was just allowed to write, so a
    /// missing row is an invariant violation rather than a "not found".
    /// </para>
    /// </remarks>
    private async Task<Dictionary<string, object>> InsertAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Dictionary<string, object> candidate, CancellationToken cancellationToken)
    {
        db.Rows(schema.Name).Add(candidate);
        await db.SaveChangesAsync(cancellationToken);

        var id = (Guid)candidate[AlvoDataContext.IdColumn];
        return await SingleAsync(db, schema, decision, context, id, lockFor: null, cancellationToken)
            ?? throw new InvalidOperationException(
                "The row this create just inserted could not be read back inside its own transaction.");
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
        EnsureNotSoftDeleted(schema);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EraseAsync(db, schema, decision, context, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The body of one delete, inside the caller's transaction: the locked pre-image, then the
    /// policy-carrying <c>DELETE</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A delete has no <c>WITH CHECK</c> — there is no post-image to check — so it needs no verdict over the
    /// pre-image, and this read exists for the <b>shape</b> rather than for a decision: PR5's outbox row and a
    /// <c>record.deleted</c> event both need the row image, and an in-transaction before-hook needs something
    /// to run over. Without the transaction, PR5's outbox row could not ride the same <c>DbTransaction</c> at
    /// all — on SQLite a second connection writing while this one holds a write transaction on the same file
    /// gets <c>SQLITE_BUSY</c>, so the happy path would deadlock rather than merely lose atomicity.
    /// </para>
    /// <para>
    /// It also gives <see cref="PreImageMutation.Delete"/> — and therefore PostgreSQL's <c>FOR UPDATE</c> —
    /// the consumer it lacked, so <see cref="IAlvoSqlDialect.RowLockClause"/>'s remarks describe a path that
    /// exists.
    /// </para>
    /// <para>
    /// Both refusals are <see cref="AlvoRecordNotFoundException"/> with no message, and they are
    /// indistinguishable on purpose: a row the caller cannot see must read exactly like one that was never
    /// there. The <c>DELETE</c> is still constrained by <c>USING</c>, so <c>rows affected == 0</c> after a
    /// successful pre-image read means a concurrent writer got there first.
    /// </para>
    /// </remarks>
    private async Task EraseAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Guid id, CancellationToken cancellationToken)
    {
        _ = await SingleAsync(
            db, schema, decision, context, id, PreImageMutation.Delete, cancellationToken, unmasked: true)
            ?? throw new AlvoRecordNotFoundException();

        var affected = await RowOf(PolicyRoot(db, schema, decision, context), id)
            .ExecuteDeleteAsync(cancellationToken);
        if (affected == 0)
        {
            throw new AlvoRecordNotFoundException();
        }
    }

    /// <summary>
    /// Refuses a delete on an entity whose schema declares <c>softDelete</c>, because this data path would
    /// <b>hard-delete</b> the row while the descriptor contract promises a recoverable one — measured on real
    /// PostgreSQL, where the row was simply gone.
    /// </summary>
    /// <remarks>
    /// The descriptor mapper already refuses <c>softDelete</c> at apply time, so this is the request-time
    /// belt for a <see cref="SchemaModel"/> that did not come through it — a host-assembled model, or F7's
    /// dynamic registry. It is the same fail-closed shape as
    /// <see cref="QueryFieldGuard.EnsureMaskable"/>, and it exists because the failure mode here is silent
    /// data loss rather than a wrong answer.
    /// </remarks>
    private static void EnsureNotSoftDeleted(EntitySchema schema)
    {
        if (schema.SoftDelete)
        {
            throw new InvalidOperationException(
                "Soft delete is not implemented, so this entity cannot be deleted: the row would be removed "
                + "outright while its schema declares the delete recoverable. Remove 'softDelete' from the "
                + "descriptor, or track the soft-delete implementation issue.");
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
    /// <remarks>
    /// Fetches one row past <see cref="AlvoQuery.Limit"/> when it is set — the over-fetch
    /// <see cref="Paginated"/> reads back to derive <see cref="AlvoPage.NextCursor"/> honestly, rather than
    /// from whether this page happened to come back exactly full.
    /// </remarks>
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
                Limit = OverFetched(query.Limit),
                Offset = query.Offset,
            },
            db.Rows(schema.Name).EntityType);

        return await Materialize(db, schema, statement).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// One past <paramref name="limit"/>, so <see cref="Paginated"/> can tell a page that ends exactly at the
    /// visible set's boundary from one with more rows after it. Clamped at <see cref="int.MaxValue"/> rather
    /// than overflowing into a negative bound value a caller who set <see cref="AlvoQuery.Limit"/> to that
    /// value could otherwise trigger.
    /// </summary>
    private static int? OverFetched(int? limit) => limit switch
    {
        null => null,
        int.MaxValue => limit,
        _ => limit + 1,
    };

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
