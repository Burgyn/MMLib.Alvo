using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Data.Common;

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
    private readonly string _idempotencyTable;

    internal EfAlvoData(
        IPolicyEngine policy,
        IPredicateEvaluator evaluator,
        IPredicateRenderer predicates,
        IFieldSqlRenderer fields,
        IAlvoSqlDialect dialect,
        AlvoDataContextFactory contexts,
        TimeProvider time,
        AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        _policy = policy;
        _evaluator = evaluator;
        _statements = new ReadStatementComposer(predicates, fields, dialect);
        _contexts = contexts;
        _time = time;
        _idempotencyTable = IdempotencyTable.NameFor(options.SchemaPrefix);
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
        AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);
        AlvoIdempotency.EnsureUsableToken(idempotency, context);

        var decision = Resolve(entity, DataOperation.Create, context);

        return idempotency is { } token
            ? await ReplayableCreateAsync(entity, values, decision, context, token, cancellationToken)
            : await CreatedAsync(entity, values, decision, context, cancellationToken);
    }

    /// <summary>One ordinary create: the authorized candidate, inserted and re-read inside one transaction.</summary>
    private async Task<AlvoRecord> CreatedAsync(
        string entity, IReadOnlyDictionary<string, object?> values, PolicyDecision decision, AlvoContext context,
        CancellationToken cancellationToken)
    {
        using var db = _contexts.Create();
        var (schema, candidate) = AuthorizedCandidate(db, entity, values, decision, context);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stored = await InsertAsync(db, schema, decision, context, candidate, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RecordMaterializer.ToRecord(stored, decision.HiddenFields);
    }

    /// <summary>
    /// The candidate row this create would insert, already refused if the payload named a field a caller may
    /// not write and already checked against <c>WITH CHECK</c> and the tenant scope.
    /// </summary>
    /// <remarks>
    /// Built per attempt rather than once, so a retried attempt re-stamps its audit instant and mints a fresh
    /// row id: the previous attempt's id was rolled back, and reusing a candidate a change tracker has already
    /// seen is how a retry turns into a second insert of the same object.
    /// </remarks>
    private (EntitySchema Schema, Dictionary<string, object> Candidate) AuthorizedCandidate(
        AlvoDataContext db, string entity, IReadOnlyDictionary<string, object?> values, PolicyDecision decision,
        AlvoContext context)
    {
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        WritePayloadGuard.EnsureWritable(values, schema, decision, isUpdate: false);

        var candidate = Candidate(db.Rows(entity).EntityType, Stamped(schema, values, context, isUpdate: false));
        EnsureWriteAllowed(decision, RecordMaterializer.ToRecord(candidate, _noMask), previous: null, context);

        return (schema, candidate);
    }

    /// <summary>
    /// One create carrying an idempotency token, retried while storage refuses the write because a rival
    /// holding the same key is mid-flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a retry rather than an error-code check.</b> The loser of the race fails one of two ways and
    /// neither is distinguishable without reading a provider-specific code, which this package does not do
    /// (see <see cref="VersionRowWriter"/>'s own translation for the precedent): on PostgreSQL it violates the
    /// idempotency table's primary key, and on SQLite it is refused with <c>database is locked</c> the moment
    /// it tries to write while the winner holds the file — before the primary key is ever consulted. Rolling
    /// back and starting over converges on both, because the next attempt's lookup finds the record the
    /// winner committed and answers as a replay.
    /// </para>
    /// <para>
    /// <b>Why a broad catch cannot become a false replay.</b> This catches any storage write failure, which
    /// includes a unique-constraint violation in the caller's <em>own</em> entity — a duplicate <c>vin</c>,
    /// say. That never turns into a replay of an unrelated row, and the reason is structural rather than a
    /// classification: the only thing a retry does is start the attempt over, and an attempt answers as a
    /// replay <b>only</b> if the lookup finds a record for this key in this scope. A duplicate <c>vin</c>
    /// commits no such record, so every attempt takes the insert path again and fails again, and the loop ends
    /// at <see cref="ExhaustedAsRetryLimit"/> with the provider's exception as the inner one. Matching this
    /// table's constraint specifically would need a provider error code, which is what
    /// <see cref="VersionRowWriter"/> deliberately does not read.
    /// </para>
    /// <para>
    /// <b>Exhaustion stays inside the port's five failure families.</b> The raw
    /// <see cref="DbException"/>/<see cref="DbUpdateException"/> used to propagate, outside the contract
    /// <see cref="IAlvoData"/> promises a request layer can map a status from — PR3's problem-details layer
    /// would have rendered a provider message as an unhandled 500. It is now an
    /// <see cref="InvalidOperationException"/>, which is that contract's family for an invariant this
    /// implementation relies on, with the provider exception preserved as the inner one.
    /// </para>
    /// </remarks>
    private async Task<AlvoRecord> ReplayableCreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, PolicyDecision decision, AlvoContext context,
        AlvoIdempotency token, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CreatedOrReplayedAsync(entity, values, decision, context, token, cancellationToken);
            }
            catch (Exception failure) when (IsStorageWriteFailure(failure))
            {
                if (attempt >= ContendedCreateAttempts)
                {
                    throw ExhaustedAsRetryLimit(failure);
                }

                await Task.Delay(_contentionBackoff * attempt, cancellationToken);
            }
        }
    }

    /// <summary>
    /// How many times a contended idempotent create starts over, and how long it waits between attempts.
    /// </summary>
    /// <remarks>
    /// Ten attempts with a linearly growing pause — about 450 ms in total. Sized so ordinary contention on a
    /// real engine cannot exhaust it: a rival's own transaction is a handful of statements, so the first pause
    /// already outlasts it, and the remaining attempts exist for a queue of rivals on one key rather than for
    /// one. Still bounded, because a loop that retries forever turns a permanently failing write into a hung
    /// request instead of an answer.
    /// </remarks>
    private const int ContendedCreateAttempts = 10;

    /// <inheritdoc cref="ContendedCreateAttempts"/>
    private static readonly TimeSpan _contentionBackoff = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The exhausted retry, as this port's own failure contract rather than the provider's exception.
    /// </summary>
    /// <param name="failure">The last storage write failure, preserved as the inner exception.</param>
    private static InvalidOperationException ExhaustedAsRetryLimit(Exception failure) =>
        new(
            $"An idempotent create was retried {ContendedCreateAttempts} times and storage refused the write "
            + "every time. The write is guarded by the idempotency table's primary key "
            + "(idempotency_key, scope), so a refusal this persistent is either sustained contention on that "
            + "one key or a constraint this create violates on its own — the inner exception says which.",
            failure);

    /// <summary>
    /// Whether <paramref name="failure"/> is storage refusing a write. <c>SaveChanges</c> wraps the provider's
    /// exception in <see cref="DbUpdateException"/> while a hand-built command throws
    /// <see cref="DbException"/> straight through, so both spellings have to be named — matching only one is
    /// how half the retry silently stops working.
    /// </summary>
    private static bool IsStorageWriteFailure(Exception failure) =>
        failure is DbException or DbUpdateException;

    /// <summary>
    /// One attempt at an idempotent create, inside one transaction: ensure the record table, look the key up,
    /// and then either replay the recorded row or insert this one and record it.
    /// </summary>
    /// <remarks>
    /// The lookup and the two inserts share the transaction the row itself is written in, which is what makes
    /// "the row exists" and "the key is recorded" one fact rather than two — a record committed without its
    /// row would answer every later replay with a row id that never existed.
    /// </remarks>
    private async Task<AlvoRecord> CreatedOrReplayedAsync(
        string entity, IReadOnlyDictionary<string, object?> values, PolicyDecision decision, AlvoContext context,
        AlvoIdempotency token, CancellationToken cancellationToken)
    {
        using var db = _contexts.Create();
        var (schema, candidate) = AuthorizedCandidate(db, entity, values, decision, context);
        await EnsureIdempotencyTableAsync(db, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var records = new IdempotencyScope(
            db.Database.GetDbConnection(), transaction.GetDbTransaction(), _idempotencyTable, token, context);

        var recorded = await records.FindAsync(cancellationToken);
        var result = recorded is { } record
            ? await ReplayedAsync(db, schema, context, record, token, cancellationToken)
            : await RecordedCreateAsync(db, schema, decision, context, candidate, records, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Inserts the row and records the key against it, in that order and in one transaction. The record's
    /// primary key is the concurrency control: a rival that already committed one for this key makes this
    /// insert fail, which is what <see cref="ReplayableCreateAsync"/> turns into a replay.
    /// </summary>
    private async Task<AlvoRecord> RecordedCreateAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Dictionary<string, object> candidate, IdempotencyScope records, CancellationToken cancellationToken)
    {
        var stored = await InsertAsync(db, schema, decision, context, candidate, cancellationToken);
        await records.InsertAsync((Guid)candidate[AlvoDataContext.IdColumn], _time.GetUtcNow(), cancellationToken);

        return RecordMaterializer.ToRecord(stored, decision.HiddenFields);
    }

    /// <summary>
    /// The answer to a replay: the recorded row, <b>re-read through this caller's current policy</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never a stored copy of the first response. Re-reading is what keeps a replay from handing back a
    /// representation the caller's policy would not produce today — a field that has since become
    /// <c>hidden</c> for them stays hidden, and a row they can no longer see is not resurrected. It also means
    /// a row that has since been deleted answers <see cref="AlvoRecordNotFoundException"/>, which is the same
    /// thing every other read of a missing row says.
    /// </para>
    /// <para>
    /// <b>Read under a freshly resolved <c>get</c> decision, never under the <c>create</c> decision this call
    /// arrived with.</b> That was a row-level authorization bypass, not a tidiness point: <c>create</c> has no
    /// stored row to filter, so <see cref="PolicyDecision.Using"/> is <see langword="null"/> by contract and
    /// the composer renders it as a constant true — a replay read that way returns the recorded row whoever
    /// owns it. Resolving <c>get</c> also gets the mask right, because <c>hidden</c> is evaluated per caller:
    /// a replay must return what a <c>GET</c> by <em>this</em> caller would.
    /// </para>
    /// <para>
    /// A caller who may create but not read therefore has their replay refused with
    /// <see cref="AlvoAuthorizationException"/> from <see cref="Resolve"/>, while their original create
    /// succeeded. That asymmetry is deliberate and is the only safe direction: a replay <em>is</em> a read of a
    /// stored row, so it must satisfy <c>get</c>, and falling back to the create decision when <c>get</c>
    /// denies is precisely the bypass above.
    /// </para>
    /// <para>
    /// A different fingerprint under the same key is refused before the row is read at all: it is not a replay,
    /// and answering with the first request's row would report success for a create that never happened.
    /// </para>
    /// </remarks>
    private async Task<AlvoRecord> ReplayedAsync(
        AlvoDataContext db, EntitySchema schema, AlvoContext context,
        IdempotencyTable.IdempotencyRecord record, AlvoIdempotency token, CancellationToken cancellationToken)
    {
        if (!token.Matches(record.Fingerprint))
        {
            throw new AlvoIdempotencyConflictException();
        }

        var read = Resolve(schema.Name, DataOperation.Get, context);
        var row = await SingleAsync(db, schema, read, context, record.RowId, lockFor: null, cancellationToken)
            ?? throw new AlvoRecordNotFoundException();

        return RecordMaterializer.ToRecord(row, read.HiddenFields);
    }

    /// <summary>
    /// Ensures the idempotency table exists, once per process and <b>before</b> the write transaction begins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Outside the transaction, deliberately: run inside it, this DDL is a serialization point that hides the
    /// primary key the concurrency control actually rests on — see
    /// <see cref="IdempotencyTable.EnsureAsync"/>, where that is measured. Outside it, the statement commits on
    /// its own, so remembering that it succeeded is honest rather than a claim a later rollback could undo.
    /// </para>
    /// <para>
    /// A plain <see langword="volatile"/> flag with no gate around it: two callers racing the first create both
    /// run <c>CREATE TABLE IF NOT EXISTS</c>, which is idempotent by construction, and a genuine collision on
    /// the very first one is a storage write failure the caller's retry already handles. A semaphore would buy
    /// nothing and would make this type disposable.
    /// </para>
    /// <para>
    /// It is only reached by a create that carries a token; an ordinary create never touches this table.
    /// </para>
    /// </remarks>
    private async Task EnsureIdempotencyTableAsync(AlvoDataContext db, CancellationToken cancellationToken)
    {
        if (_idempotencyTableEnsured)
        {
            return;
        }

        var connection = db.Database.GetDbConnection();
        await RelationalSqlBatch.OpenAsync(connection, cancellationToken);
        await IdempotencyTable.EnsureAsync(connection, _idempotencyTable, cancellationToken);
        _idempotencyTableEnsured = true;
    }

    /// <inheritdoc cref="EnsureIdempotencyTableAsync"/>
    private volatile bool _idempotencyTableEnsured;

    /// <summary>
    /// One transaction's view of the idempotency table: the connection, the transaction, the table name and
    /// the token, bound together so the four of them are not threaded through every call site separately.
    /// </summary>
    /// <remarks>
    /// A struct over the statements in <see cref="IdempotencyTable"/> rather than a second place that composes
    /// SQL — that type stays the only one that writes this table's text, and this only stops four arguments
    /// from being repeated at both call sites.
    /// </remarks>
    private readonly struct IdempotencyScope(
        DbConnection connection, DbTransaction transaction, string tableName, AlvoIdempotency token,
        AlvoContext context)
    {
        /// <summary>
        /// The key's scope, from the port's own authority rather than assembled here — see
        /// <see cref="AlvoIdempotency.IdentityOf"/> for why the acting user is part of it.
        /// </summary>
        private string Scope => AlvoIdempotency.IdentityOf(context);

        internal Task<IdempotencyTable.IdempotencyRecord?> FindAsync(CancellationToken cancellationToken) =>
            IdempotencyTable.FindAsync(connection, transaction, tableName, token.Key, Scope, cancellationToken);

        internal Task InsertAsync(Guid rowId, DateTimeOffset createdAt, CancellationToken cancellationToken) =>
            IdempotencyTable.InsertAsync(
                connection, transaction, tableName, token, Scope, rowId, createdAt, cancellationToken);
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
            throw new AlvoAuthorizationException(AlvoAuthorizationException.WriteRejectedByPolicy);
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
        AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Update, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        WritePayloadGuard.EnsureWritable(values, schema, decision, isUpdate: true);
        AlvoPrecondition.EnsureSupported(precondition, schema);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var postImage = await WriteAsync(
            db, schema, decision, context, id, Stamped(schema, values, context, isUpdate: true), precondition,
            cancellationToken);
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
        string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Delete, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        EnsureNotSoftDeleted(schema);
        AlvoPrecondition.EnsureSupported(precondition, schema);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EraseAsync(db, schema, decision, context, id, precondition, cancellationToken);
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
        Guid id, AlvoPrecondition? precondition, CancellationToken cancellationToken)
    {
        var stored = await SingleAsync(
            db, schema, decision, context, id, PreImageMutation.Delete, cancellationToken, unmasked: true)
            ?? throw new AlvoRecordNotFoundException();
        AlvoPrecondition.EnsureMatches(precondition, StoredVersion(schema, stored));

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
        Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition,
        CancellationToken cancellationToken)
    {
        var stored = await SingleAsync(
            db, schema, decision, context, id, PreImageMutation.Update, cancellationToken, unmasked: true)
            ?? throw new AlvoRecordNotFoundException();
        AlvoPrecondition.EnsureMatches(precondition, StoredVersion(schema, stored));

        var preImage = RecordMaterializer.ToRecord(stored, _noMask);
        EnsureWriteAllowed(decision, Merge(preImage, values), preImage, context);

        if (await AffectedAsync(db, schema, decision, context, id, values, cancellationToken) == 0)
        {
            throw new AlvoRecordNotFoundException();
        }

        return await SingleAsync(db, schema, decision, context, id, lockFor: null, cancellationToken)
            ?? throw new AlvoRecordNotFoundException();
    }

    /// <summary>
    /// The version the row-locked pre-image carries, or <see langword="null"/> when the entity keeps none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read off the pre-image the <c>WITH CHECK</c> verdict is already reached over, never with a second
    /// query. That is what makes the comparison unraceable: the row is held under the driver's lock
    /// (<see cref="PreImageMutation"/>) from this read until the write commits, so nothing can advance the
    /// version between "the version matches" and "the write landed". A version read before the transaction —
    /// or on another connection — would approve exactly the lost update the precondition exists to stop.
    /// </para>
    /// <para>
    /// <b>Where this sits in the order is the contract.</b> The pre-image read is already constrained by
    /// <c>USING</c>, so a row this caller cannot see has raised
    /// <see cref="AlvoRecordNotFoundException"/> before the version is ever looked at — invisibility outranks
    /// the precondition, and the precondition can never answer "does that row exist". It runs <em>before</em>
    /// <c>WITH CHECK</c> for the opposite reason: a stale precondition means the caller's whole patch was
    /// computed against a row that no longer exists in that form, so a verdict over their merged post-image
    /// would be a verdict about a merge that should not happen. Both are already-visible-row decisions, so
    /// neither ordering leaks anything; this one just answers the more useful of the two.
    /// </para>
    /// </remarks>
    private static object? StoredVersion(EntitySchema schema, Dictionary<string, object> preImage) =>
        AlvoManagedColumns.VersionColumn(schema) is { } column ? preImage.GetValueOrDefault(column) : null;

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
