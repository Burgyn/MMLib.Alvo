using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Collections.Frozen;
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

    /// <summary>
    /// The <c>before*</c> hook pipeline, called from <b>inside</b> every write transaction this class opens.
    /// </summary>
    /// <remarks>
    /// A port and not a call into the core, because this class is <see langword="internal"/> to a driver that
    /// depends on <c>MMLib.Alvo.Abstractions</c> alone — so both shipped engines and any out-of-repo one run
    /// the same pipeline rather than each growing its own. Its method is synchronous, which is what makes a
    /// network call from a hook holding this transaction's locks inexpressible; see
    /// <see cref="IBeforeHookRunner"/>.
    /// </remarks>
    private readonly IBeforeHookRunner _hooks;
    private readonly ReadStatementComposer _statements;

    /// <summary>
    /// The driver's SQL seam, kept whole rather than only handed to the composer: it also owns the
    /// engine-specific decoding of a constraint violation — see
    /// <see cref="ConstraintViolationTranslator"/>.
    /// </summary>
    private readonly IAlvoSqlDialect _dialect;

    /// <summary>
    /// The rollup maintainer, called inside every child write's own transaction — see
    /// <see cref="RollupRecompute"/> for why the parent's lock has to precede the recompute.
    /// </summary>
    private readonly RollupRecompute _rollups;
    private readonly AlvoDataContextFactory _contexts;
    private readonly TimeProvider _time;
    private readonly string _idempotencyTable;
    private readonly string _outboxTable;

    internal EfAlvoData(
        IPolicyEngine policy,
        IPredicateEvaluator evaluator,
        IBeforeHookRunner hooks,
        IPredicateRenderer predicates,
        IFieldSqlRenderer fields,
        IAlvoSqlDialect dialect,
        AlvoDataContextFactory contexts,
        TimeProvider time,
        AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        _policy = policy;
        _evaluator = evaluator;
        _hooks = hooks;
        _statements = new ReadStatementComposer(predicates, fields, dialect);
        _dialect = dialect;
        _rollups = new RollupRecompute(dialect, fields);
        _contexts = contexts;
        _time = time;
        _idempotencyTable = IdempotencyTable.NameFor(options.SchemaPrefix);
        _outboxTable = OutboxTable.NameFor(options.SchemaPrefix);
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
        AlvoQuery.EnsureProjectionIsSane(query);

        using var db = _contexts.Create();
        var entity = Entity(db, query.Entity);
        QueryFieldGuard.EnsureAvailable(QueryFields(query), entity, decision.HiddenFields);

        var anchor = await AnchorAsync(db, entity, decision, context, query, cancellationToken);
        var options = ReadOptions(query, anchor, entity);
        var total = await TotalCountAsync(db, entity, decision, context, query, options, cancellationToken);

        // A cursor this provider never issued — stale, forged, or from another tenant — finds no anchor and
        // is answered with an empty page rather than the first one. The count still comes back, because it is
        // a property of the query's visible set and not of the window the cursor failed to open: answering
        // null there would make the HTTP layer's `Preference-Applied: count=exact` a lie.
        if (query.After is not null && anchor is null)
        {
            return AlvoPage.Empty with { TotalCount = total };
        }

        var fetched = await PageAsync(db, entity, decision, context, options, cancellationToken);
        var (kept, nextCursor) = Paginated(fetched, query.Limit);
        return new AlvoPage
        {
            Items = [.. kept.Select(row => RecordMaterializer.ToRecord(row, decision.HiddenFields, options.Unselected))],
            NextCursor = nextCursor,
            TotalCount = total,
        };
    }

    /// <summary>
    /// How many rows the query matches in total, or <see langword="null"/> when
    /// <see cref="AlvoQuery.IncludeTotalCount"/> did not ask — in which case <b>no statement is composed and
    /// none is executed</b>, which is what makes the count opt-in rather than merely optional.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run through EF's own <c>SqlQueryRaw</c> rather than a raw command on the context's connection, so the
    /// count binds its values through the same <see cref="PredicateParameterBinder"/> as the page, needs no
    /// second execution path, and — the reason that decides it — is <b>visible to the same diagnostic
    /// listener the statement suite observes</b>. "The count carries the policy predicate" is a claim no
    /// returned number can carry, so it has to be assertable on the statement.
    /// </para>
    /// <para>
    /// <c>ToListAsync</c> rather than <c>SingleAsync</c>: a <c>SqlQueryRaw</c> with nothing composed over it
    /// is emitted verbatim, while composing a LINQ operator wraps it in a subquery whose output column EF
    /// then requires to be named <c>Value</c> — an EF artifact in a statement that is otherwise Alvo's own.
    /// The result set is one row by construction, so <c>Single</c> here is a fact about <c>COUNT(*)</c>, not
    /// a bound imposed on the query.
    /// </para>
    /// </remarks>
    private async Task<long?> TotalCountAsync(
        AlvoDataContext db, EntitySchema? entity, PolicyDecision decision, AlvoContext context,
        AlvoQuery query, ReadStatementComposer.ReadStatementOptions options, CancellationToken cancellationToken)
    {
        if (!query.IncludeTotalCount)
        {
            return null;
        }

        var schema = entity ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        var statement = _statements.ComposeCount(schema, decision, context, options);
        var parameters = new PredicateParameterBinder(db).Bind(
            db.Rows(schema.Name).EntityType, statement.Parameters);

        var counted = await db.Database.SqlQueryRaw<long>(statement.Sql, parameters).ToListAsync(cancellationToken);
        return counted.Single();
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
        return row is null ? null : RecordMaterializer.ToRecord(row, decision.HiddenFields, FrozenSet<string>.Empty);
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

    /// <summary>
    /// One ordinary create: the authorized candidate, inserted, re-read and emitted inside one transaction.
    /// </summary>
    private async Task<AlvoRecord> CreatedAsync(
        string entity, IReadOnlyDictionary<string, object?> values, PolicyDecision decision, AlvoContext context,
        CancellationToken cancellationToken)
    {
        using var db = _contexts.Create();
        var now = WriteInstantNow();
        var (schema, candidate) = AuthorizedCandidate(db, entity, values, decision, context, now);
        await EnsureOutboxTableAsync(db, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        candidate = RunBeforeCreate(db, schema, decision, context, candidate, now);
        var stored = await InsertAsync(db, schema, decision, context, candidate, cancellationToken);
        await RecomputeRollupsAsync(db, schema, [stored!], cancellationToken);
        await EmitAsync(
            db, transaction, schema, OutboxOperation.Created, context, now, Unmasked(stored), preImage: null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RecordMaterializer.ToRecord(stored, decision.HiddenFields, FrozenSet<string>.Empty);
    }

    /// <summary>
    /// Runs the entity's <c>beforeCreate</c> hooks over the candidate and re-reaches the write's own verdict
    /// over whatever they patched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called from inside the transaction at every create site, and never where the candidate is built.</b>
    /// <see cref="AuthorizedCandidate"/> runs before <c>BeginTransactionAsync</c> — a hook placed there would
    /// have nothing to roll back, so a <c>reject</c> would refuse a write whose row was already committed on
    /// the next site to open a transaction, and a budget the hook overran would be spent outside the scope
    /// that could undo it. Inside the transaction, a refusal is a rolled-back write with no row and no outbox
    /// event, which is what the DoD asks of it.
    /// </para>
    /// <para>
    /// <b>The verdict is re-reached and the caller's payload guard is not, and the asymmetry is the ruling.</b>
    /// <see cref="WritePayloadGuard"/> judges <em>a caller's</em> keys — framework-managed columns, fields a
    /// policy froze as <c>readOnly</c> — and a hook is not the caller: re-running it would refuse a hook
    /// legitimately setting a field callers may not write, which is one of the two things a before-hook exists
    /// for. <see cref="EnsureWriteAllowed"/> judges something else entirely: the <em>row</em> the write will
    /// store, against <c>WITH CHECK</c> and the tenant scope. A patch reaching storage unjudged would be a
    /// caller-reachable authorization bypass — a hook writing <c>owner_id</c> from a field the caller controls
    /// would place a row the <c>create</c> rule refuses — so the post-image verdict runs again over exactly
    /// what will be written.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The candidate to insert: the one passed in when no hook patched anything, and a patched copy otherwise.
    /// A copy rather than an in-place edit because a patch may write <see langword="null"/>, which the bag's
    /// non-nullable value type cannot hold — the key is left out instead, which is
    /// <see cref="WritePropertyBag"/>'s own rule for an insert.
    /// </returns>
    private Dictionary<string, object> RunBeforeCreate(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Dictionary<string, object> candidate, DateTimeOffset now)
    {
        var patch = _hooks.Run(schema.Name, DataOperation.Create, Unmasked(candidate), previous: null, context, now);
        if (patch.Count == 0)
        {
            return candidate;
        }

        var patched = Patched(db.Rows(schema.Name).EntityType, candidate, patch);
        EnsureWriteAllowed(decision, Unmasked(patched), previous: null, context);

        return patched;
    }

    /// <summary>
    /// Applies a hook's patch to the candidate bag, through <see cref="WritePropertyBag"/> — the same funnel a
    /// caller's own value goes through, so a mutated value reaches the column as a bound parameter in that
    /// column's own representation and never as interpolated text.
    /// </summary>
    /// <remarks>
    /// A field the patch set to <see langword="null"/> is <b>absent</b> from the result rather than present
    /// with a null, which is <see cref="WritePropertyBag"/>'s own rule for an insert: the bag's value type is
    /// non-nullable, and an absent key already means "leave the column at its default", which for a nullable
    /// column is a SQL null. Built as a new bag rather than edited in place, so this path composes no
    /// change-tracker vocabulary at all.
    /// </remarks>
    private static Dictionary<string, object> Patched(
        IEntityType rows, Dictionary<string, object> candidate, IReadOnlyDictionary<string, object?> patch)
    {
        var patched = candidate
            .Where(entry => !IsNulledBy(patch, entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        foreach (var (field, value) in WritePropertyBag.For(rows, patch))
        {
            patched[field] = value;
        }

        return patched;
    }

    /// <summary>Whether <paramref name="patch"/> sets <paramref name="field"/> to <see langword="null"/>.</summary>
    private static bool IsNulledBy(IReadOnlyDictionary<string, object?> patch, string field) =>
        patch.TryGetValue(field, out var value) && value is null;

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
        AlvoContext context, DateTimeOffset now)
    {
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        WritePayloadGuard.EnsureWritable(values, schema, decision, isUpdate: false);

        var candidate = Candidate(db.Rows(entity).EntityType, Stamped(schema, values, context, now, isUpdate: false));
        EnsureWriteAllowed(decision, Unmasked(candidate), previous: null, context);

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
    /// never turns into a replay of an unrelated row, and the reason is structural rather than a
    /// classification: the only thing a retry does is start the attempt over, and an attempt answers as a
    /// replay <b>only</b> if the lookup finds a record for this key in this scope.
    /// </para>
    /// <para>
    /// <b>A duplicate in the caller's own entity no longer reaches this loop at all (#138).</b> It used to:
    /// the entity's own insert failed with the provider's exception, every one of the ten attempts took the
    /// insert path again and failed again, and the loop ended at <see cref="ExhaustedAsRetryLimit"/> — ten
    /// transactions and about 450 ms spent re-answering a question whose answer could not change. That write
    /// now goes through <see cref="ConstraintViolationTranslator"/>, which turns a recognised violation into
    /// <see cref="AlvoConstraintViolationException"/>; that is not a <see cref="DbException"/>, so
    /// <see cref="IsStorageWriteFailure"/> does not match it and it leaves on the first attempt. The
    /// idempotency record's own insert is deliberately <em>not</em> translated — a rival winning that primary
    /// key is exactly what this loop exists to converge on — so the retry it was written for is unaffected.
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
        var now = WriteInstantNow();
        var (schema, candidate) = AuthorizedCandidate(db, entity, values, decision, context, now);
        await EnsureIdempotencyTableAsync(db, cancellationToken);
        await EnsureOutboxTableAsync(db, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var records = new IdempotencyScope(
            db.Database.GetDbConnection(), transaction.GetDbTransaction(), _idempotencyTable, token, context);

        var recorded = await records.FindAsync(cancellationToken);
        var result = recorded is { } record
            ? await ReplayedAsync(db, schema, context, record, token, cancellationToken)
            : await RecordedCreateAsync(
                db, transaction, schema, decision, context, candidate, records, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Inserts the row, emits its event, and records the key against it — in that order and in one
    /// transaction. The record's primary key is the concurrency control: a rival that already committed one
    /// for this key makes that last insert fail, which is what <see cref="ReplayableCreateAsync"/> turns into
    /// a replay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The event is written before the record, and that ordering is the one place the atomicity claim is
    /// observable on a production path.</b> The record's primary key is the only write here that a rival can
    /// make fail <em>after</em> the event exists, so emitting first is what makes "the row and its event
    /// commit together or not at all" a fact a test can reach: the loser rolls the pair back and its retry
    /// answers as a replay, so two clients racing one key produce one row and one event. Emitted after the
    /// record instead, the loser would never have emitted at all and an outbox row that did not ride the
    /// transaction would look identical to one that did.
    /// </para>
    /// <para>
    /// The instant is the write's own, threaded in rather than read again here: the row's audit stamp, the
    /// event's <c>time</c> and the record's <c>created_at</c> are one instant.
    /// </para>
    /// <para>
    /// <b>This is where the <c>beforeCreate</c> hooks run on the idempotent path, and
    /// <see cref="CreatedOrReplayedAsync"/> is deliberately not.</b> That method opens the transaction and then
    /// branches on whether a record for the key already exists, so a hook called there would run on a
    /// <em>replay</em> too — doubling a <c>mutate</c> whose value the first attempt already stored, and letting
    /// a <c>reject</c> refuse a retry of a create the caller has already been told succeeded. A hook belongs on
    /// the branch that writes a row, which is this one.
    /// </para>
    /// </remarks>
    private async Task<AlvoRecord> RecordedCreateAsync(
        AlvoDataContext db, IDbContextTransaction transaction, EntitySchema schema, PolicyDecision decision,
        AlvoContext context, Dictionary<string, object> candidate, IdempotencyScope records, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        candidate = RunBeforeCreate(db, schema, decision, context, candidate, now);
        var stored = await InsertAsync(db, schema, decision, context, candidate, cancellationToken);
        await RecomputeRollupsAsync(db, schema, [stored!], cancellationToken);
        await EmitAsync(
            db, transaction, schema, OutboxOperation.Created, context, now, Unmasked(stored), preImage: null,
            cancellationToken);
        await records.InsertAsync([(Guid)candidate[AlvoDataContext.IdColumn]], now, cancellationToken);

        return RecordMaterializer.ToRecord(stored, decision.HiddenFields, FrozenSet<string>.Empty);
    }

    /// <summary>
    /// The answer to a replay: the recorded row, <b>re-read through this caller's current policy</b> — or,
    /// when that policy refuses <c>get</c> outright, the id alone, disclosed with no row read at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never a stored copy of the first response. Re-reading is what keeps a replay from handing back a
    /// representation the caller's policy would not produce today — a field that has since become
    /// <c>hidden</c> for them stays hidden, and a row they can no longer see is not resurrected. It also means
    /// a row that has since been deleted, or that a <em>configured</em> <c>get</c>'s own predicate excludes
    /// (an entity whose rule is <c>USING (status == 'published')</c>, say), answers
    /// <see cref="AlvoRecordNotFoundException"/>, which is the same thing every other read of a missing row
    /// says. That sibling case is deliberately left exactly as it stands: telling "invisible to me" apart from
    /// "genuinely gone since" would need a second, policy-free read, and refusing to add one is the more
    /// conservative of the two errors.
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
    /// <b>A caller who may create but not read is no longer refused for retrying.</b> When <c>get</c> is
    /// denied outright — no policy allows it at all, so <see cref="PolicyDecision.IsDenied"/> is
    /// <see langword="true"/> before any row is touched — the retry must not be worse than the create it
    /// replays: it answers with an <see cref="AlvoRecord"/> carrying only <see cref="AlvoManagedColumns.Id"/>,
    /// taken from <paramref name="record"/>'s own row list. The safety argument rests on
    /// <see cref="AlvoIdempotency.IdentityOf"/>: the record is keyed on the key, the tenant and the acting
    /// user, so a match <em>proves this caller created that row</em> — the id disclosed is exactly the id
    /// their own original 201 already gave them, in the body and in <c>Location</c>, and nothing more is
    /// disclosed because no field of the row is ever read. This must never fall back to the <c>create</c>
    /// decision to produce that id — doing so would read the row under the predicate-free decision the
    /// paragraph above exists to forbid, even if every field but <c>id</c> were then discarded.
    /// </para>
    /// <para>
    /// A different fingerprint under the same key is refused before the row is read at all: it is not a replay,
    /// and answering with the first request's row would report success for a create that never happened.
    /// </para>
    /// <para>
    /// <b>A replay runs no <c>beforeCreate</c> hook</b>, and that is the same argument this whole method rests
    /// on: nothing is re-done, only re-read. The hooks ran on the write that produced this record, so running
    /// them again would apply a second <c>mutate</c> on top of a value already stored, and would let a
    /// <c>reject</c> refuse a retry of a create the caller was already told succeeded.
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

        var read = _policy.Resolve(schema.Name, DataOperation.Get, context);
        if (read.IsDenied)
        {
            return IdOnly(RecordedRow(record));
        }

        var row = await SingleAsync(db, schema, read, context, RecordedRow(record), lockFor: null, cancellationToken)
            ?? throw new AlvoRecordNotFoundException();

        return RecordMaterializer.ToRecord(row, read.HiddenFields, FrozenSet<string>.Empty);
    }

    /// <summary>The one row a single-row write's record names.</summary>
    /// <remarks>
    /// An empty list is a broken invariant of this file rather than a caller error — every write records at
    /// least one row — so it is raised loudly (family 5, rendered 500) rather than answered as a miss, which
    /// would silently re-execute a write the caller has already been told succeeded.
    /// </remarks>
    /// <param name="record">The record this replay matched.</param>
    private static Guid RecordedRow(IdempotencyTable.IdempotencyRecord record) =>
        record.RowIds.Count > 0
            ? record.RowIds[0]
            : throw new InvalidOperationException(
                "An idempotency record names no row. Every write records at least one, so an empty list means "
                + "the record was written by something other than this port's write paths.");

    /// <summary>
    /// The narrowest possible answer to a replay: <paramref name="rowId"/> and nothing else, from the
    /// idempotency record already in hand.
    /// </summary>
    /// <remarks>
    /// An id-only <see cref="AlvoRecord"/> rather than an empty one or a <see langword="null"/> return,
    /// because <see cref="IAlvoData.CreateAsync"/> returns a non-nullable <see cref="AlvoRecord"/> — widening
    /// it to <c>AlvoRecord?</c> for one caller, or inventing a sentinel value, would cost this port a fourth
    /// contract change in a PR that has already taken three. An id-only record needs neither, and discloses
    /// nothing the caller's own <c>Location</c> header does not already carry.
    /// </remarks>
    /// <param name="rowId">The row id the idempotency record already names.</param>
    private static AlvoRecord IdOnly(Guid rowId) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal) { [AlvoDataContext.IdColumn] = rowId });

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

        internal Task InsertAsync(
            IReadOnlyList<Guid> rowIds, DateTimeOffset createdAt, CancellationToken cancellationToken) =>
            IdempotencyTable.InsertAsync(
                connection, transaction, tableName, token, Scope, rowIds, createdAt, cancellationToken);
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
    /// <para>
    /// <b>Read unmasked, then masked in memory for the caller</b> — see <see cref="Unmasked"/> for why the
    /// write path is the one read that cannot use the null projection.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<string, object>> InsertAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Dictionary<string, object> candidate, CancellationToken cancellationToken)
    {
        db.Rows(schema.Name).Add(candidate);
        await ConstraintViolationTranslator.TranslatedAsync(
            () => db.SaveChangesAsync(cancellationToken), _dialect, db.Rows(schema.Name).EntityType, schema);

        var id = (Guid)candidate[AlvoDataContext.IdColumn];
        return await SingleAsync(db, schema, decision, context, id, lockFor: null, cancellationToken, unmasked: true)
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

    /// <summary>
    /// Recomputes every rollup this child write can have changed, inside the write's own transaction and
    /// <b>after</b> the child row has been written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One method rather than four call sites' worth of the same three lines, and the images are what differ:
    /// a create has only a post-image, a delete only a pre-image, and an <em>update</em> has both — which is
    /// the case that matters, because the foreign key is writable, so moving a child from one parent to
    /// another changes two aggregates and only the pre-image knows about the first.
    /// </para>
    /// <para>
    /// The fast path is a schema question, not a row question: an entity nothing rolls up issues no statement
    /// at all, which is nearly every write this port performs.
    /// </para>
    /// </remarks>
    /// <param name="db">The context whose open transaction the recompute joins.</param>
    /// <param name="schema">The child entity that was written.</param>
    /// <param name="images">The child row's images — post, pre, or both.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    private Task RecomputeRollupsAsync(
        AlvoDataContext db, EntitySchema schema, IReadOnlyList<IReadOnlyDictionary<string, object?>> images,
        CancellationToken cancellationToken) =>
        RollupRecompute.IsRolledUp(db.AppliedSchema, schema)
            ? _rollups.ForChildWriteAsync(db, schema, images, cancellationToken)
            : Task.CompletedTask;

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
        AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);
        AlvoIdempotency.EnsureUsableToken(idempotency, context);

        var decision = Resolve(entity, DataOperation.Update, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        WritePayloadGuard.EnsureWritable(values, schema, decision, isUpdate: true);
        AlvoPrecondition.EnsureSupported(precondition, schema);

        var now = WriteInstantNow();
        await EnsureOutboxTableAsync(db, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (preImage, postImage) = await WriteAsync(
            db, schema, decision, context, id, Stamped(schema, values, context, now, isUpdate: true), precondition,
            now, cancellationToken);
        await EmitAsync(
            db, transaction, schema, OutboxOperation.Updated, context, now, Unmasked(postImage), preImage,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RecordMaterializer.ToRecord(postImage, decision.HiddenFields, FrozenSet<string>.Empty);
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
    /// <param name="schema">The entity being written.</param>
    /// <param name="values">The caller's own payload.</param>
    /// <param name="context">The caller the write is performed as.</param>
    /// <param name="now">The write's own instant, read once by the site and shared with the emit.</param>
    /// <param name="isUpdate">Whether this is an update rather than a create.</param>
    private static IReadOnlyDictionary<string, object?> Stamped(
        EntitySchema schema, IReadOnlyDictionary<string, object?> values, AlvoContext context, DateTimeOffset now,
        bool isUpdate) =>
        AlvoAuditStamp.Applied(schema, values, context, new WriteInstant(now), isUpdate);

    /// <summary>
    /// The instant one write happens at: this store's clock, at the precision the row it is about to stamp
    /// can hold.
    /// </summary>
    /// <remarks>
    /// One method rather than a bare clock read at each of the four write sites, because every one of them
    /// hands the value it reads to <em>both</em> the audit stamp and the event it emits, and the two are only
    /// the same instant if the value survives being stored — a <c>timestamptz</c> keeps microseconds and a
    /// .NET clock keeps 100-nanosecond ticks. See <see cref="StoredInstant.Storable"/> for the measurement and
    /// for why the stored value is the authoritative one.
    /// </remarks>
    private DateTimeOffset WriteInstantNow() => StoredInstant.Storable(_time.GetUtcNow());

    /// <summary>
    /// The write's own instant, in the shape <see cref="AlvoAuditStamp.Applied"/> takes it.
    /// </summary>
    /// <remarks>
    /// One write is one instant (<c>docs/architecture/data-path.md</c>, <em>Every timestamp is one
    /// instant</em>), and this write now has a second reader of it: the event it emits carries the same
    /// <c>time</c> the audit stamp recorded, and the same millisecond is embedded in the event's id. So the
    /// clock is read once at the site and handed to the stamp through this, rather than the stamp and the emit
    /// each reading it. The alternative — widening the port's <see cref="AlvoAuditStamp.Applied"/> with an
    /// instant overload — would be a public contract change for a need that is entirely this driver's.
    /// </remarks>
    /// <param name="instant">The instant every read of this clock answers.</param>
    private sealed class WriteInstant(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(
        string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition = null,
        AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);
        AlvoIdempotency.EnsureUsableToken(idempotency, context);

        var decision = Resolve(entity, DataOperation.Delete, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        EnsureNotSoftDeleted(schema);
        AlvoPrecondition.EnsureSupported(precondition, schema);

        var now = WriteInstantNow();
        await EnsureOutboxTableAsync(db, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var preImage = await EraseAsync(db, schema, decision, context, id, precondition, now, cancellationToken);
        await EmitAsync(
            db, transaction, schema, OutboxOperation.Deleted, context, now, postImage: null, preImage,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The body of one delete, inside the caller's transaction: the locked pre-image, then the
    /// policy-carrying <c>DELETE</c>. Returns the pre-image, which is the whole of what the event carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A delete has no <c>WITH CHECK</c> — there is no post-image to check — so it needs no verdict over the
    /// pre-image, and this read exists for the <b>shape</b> rather than for a decision: the outbox row and its
    /// <c>entity.{entity}.deleted</c> event both need the row image, and an in-transaction before-hook needs
    /// something to run over. Without the transaction, the outbox row could not ride the same
    /// <c>DbTransaction</c> at all — on SQLite a second connection writing while this one holds a write
    /// transaction on the same file gets <c>SQLITE_BUSY</c>, so the happy path would deadlock rather than
    /// merely lose atomicity.
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
    private async Task<AlvoRecord> EraseAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Guid id, AlvoPrecondition? precondition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await SingleAsync(
            db, schema, decision, context, id, PreImageMutation.Delete, cancellationToken, unmasked: true)
            ?? throw new AlvoRecordNotFoundException();
        AlvoPrecondition.EnsureMatches(precondition, StoredVersion(schema, stored));
        RunBeforeDelete(schema, context, Unmasked(stored), now);

        // A `ref` declaring onDelete: "restrict" is the descriptor ASKING the store to refuse this, so the
        // refusal is a conflict the caller can act on rather than a broken invariant — hence the translation.
        var affected = await ConstraintViolationTranslator.TranslatedAsync(
            () => RowOf(PolicyRoot(db, schema, decision, context), id).ExecuteDeleteAsync(cancellationToken),
            _dialect,
            db.Rows(schema.Name).EntityType,
            schema);
        if (affected == 0)
        {
            throw new AlvoRecordNotFoundException();
        }

        await RecomputeRollupsAsync(db, schema, [stored!], cancellationToken);

        return Unmasked(stored);
    }

    /// <summary>
    /// Runs the entity's <c>beforeDelete</c> hooks over the row-locked pre-image, so a <c>reject</c> refuses
    /// the delete before the <c>DELETE</c> is issued and inside the transaction that would have committed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pre-image is passed as <em>both</em> images. A delete produces no post-image, so the compiler
    /// refuses <c>new.</c> and <c>changed(...)</c> in a <c>beforeDelete</c> expression — and a bare field
    /// reference, which resolves against the current image, has to read the row being removed rather than
    /// nothing at all.
    /// </para>
    /// <para>
    /// A patch here is an invariant violation and not an author's mistake: a <c>mutate</c> under
    /// <c>beforeDelete</c> is refused when the descriptor is applied, so a non-empty patch means the compiler
    /// and this path disagree. It is raised rather than ignored, because silently dropping it is how the
    /// refusal would come to be untrue without anything failing.
    /// </para>
    /// </remarks>
    private void RunBeforeDelete(
        EntitySchema schema, AlvoContext context, AlvoRecord preImage, DateTimeOffset now)
    {
        var patch = _hooks.Run(schema.Name, DataOperation.Delete, preImage, preImage, context, now);
        if (patch.Count > 0)
        {
            throw new InvalidOperationException(
                "A 'beforeDelete' hook produced a payload patch, which no delete can write. A 'mutate' on that "
                + "hook point is refused when the descriptor is applied, so this means the hook compiler and "
                + "this write path disagree about what a delete may carry.");
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
    /// <remarks>
    /// <para>
    /// Both images leave with the result, because both are already in hand and the event needs both — the
    /// pre-image the <c>WITH CHECK</c> verdict was reached over, and the post-image the caller is answered
    /// with. That is what makes the emit cost this path no extra read.
    /// </para>
    /// <para>
    /// <b>The <c>beforeUpdate</c> hooks run here and not in <see cref="UpdateAsync"/>, because this is where
    /// both row images exist.</b> A hook's <c>old.</c> references and its <c>changed(...)</c> calls need the
    /// in-transaction, row-locked pre-image — a pre-image read before the transaction, or on another
    /// connection, would let the row advance between what the hook judged and what the write stored. Running
    /// before <see cref="EnsureWriteAllowed"/> is likewise deliberate: the verdict then judges the patched
    /// post-image, which is what will actually be written.
    /// </para>
    /// </remarks>
    private async Task<(AlvoRecord PreImage, Dictionary<string, object> PostImage)> WriteAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await SingleAsync(
            db, schema, decision, context, id, PreImageMutation.Update, cancellationToken, unmasked: true)
            ?? throw new AlvoRecordNotFoundException();
        AlvoPrecondition.EnsureMatches(precondition, StoredVersion(schema, stored));

        var preImage = Unmasked(stored);
        var vetted = RunBeforeUpdate(schema, context, preImage, values, now);
        EnsureWriteAllowed(decision, Merge(preImage, vetted), preImage, context);

        if (await AffectedAsync(db, schema, decision, context, id, vetted, cancellationToken) == 0)
        {
            throw new AlvoRecordNotFoundException();
        }

        var postImage = await SingleAsync(
            db, schema, decision, context, id, lockFor: null, cancellationToken, unmasked: true)
            ?? throw new AlvoRecordNotFoundException();
        await RecomputeRollupsAsync(db, schema, [preImage.Values, postImage!], cancellationToken);

        return (preImage, postImage);
    }

    /// <summary>
    /// Runs the entity's <c>beforeUpdate</c> hooks over the merged post-image and returns the payload the
    /// <c>UPDATE</c> should carry — the caller's, with whatever the hooks patched on top.
    /// </summary>
    /// <remarks>
    /// The hooks judge the <b>complete</b> post-image and not the caller's partial payload, for the reason
    /// <see cref="EnsureWriteAllowed"/> does: a field the caller did not mention has to read as its stored
    /// value, or a condition over one unrelated field would see a null. A patched <see langword="null"/>
    /// survives into the setters here — unlike on the insert path — because <c>ExecuteUpdate</c> writes it as
    /// a real <c>SET column = NULL</c>, which is what an author asking for null on an update means.
    /// </remarks>
    private IReadOnlyDictionary<string, object?> RunBeforeUpdate(
        EntitySchema schema, AlvoContext context, AlvoRecord preImage,
        IReadOnlyDictionary<string, object?> values, DateTimeOffset now)
    {
        var patch = _hooks.Run(
            schema.Name, DataOperation.Update, Merge(preImage, values), preImage, context, now);
        if (patch.Count == 0)
        {
            return values;
        }

        var patched = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        foreach (var (field, value) in patch)
        {
            patched[field] = value;
        }

        return patched;
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

    private Task<int> AffectedAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Guid id, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken)
        => ConstraintViolationTranslator.TranslatedAsync(
            () => RowOf(PolicyRoot(db, schema, decision, context), id)
                .ExecuteUpdateAsync(UpdateSetterFactory.For(schema, values), cancellationToken),
            _dialect,
            db.Rows(schema.Name).EntityType,
            schema);

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

    /// <summary>
    /// Every caller-supplied field name this read is about to reference — filter terms, sort keys and the
    /// projection alike.
    /// </summary>
    /// <remarks>
    /// <b>The projection is fed through the same guard rather than checked separately</b>, because it is
    /// the same kind of string and earns the same refusal: naming a masked field in a projection is the
    /// identical oracle as naming one in a filter, and one feeder is what keeps the two answers
    /// byte-identical. This is also what makes <see cref="AlvoQuery.Select"/> non-advisory at the port —
    /// a direct caller now either gets the projection or gets a refusal.
    /// </remarks>
    private static IEnumerable<string> QueryFields(AlvoQuery query) =>
        AlvoFilter.ReferencedFields(query.Filter)
            .Concat(query.Sort.Select(sort => sort.Field))
            .Concat(query.Select ?? []);

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
        ReadStatementComposer.ReadStatementOptions options, CancellationToken cancellationToken)
    {
        var schema = entity ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        var statement = _statements.Compose(
            schema, decision, context, options, db.Rows(schema.Name).EntityType);

        return await Materialize(db, schema, statement).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The <b>one</b> options record a list read composes from, built once and handed to both the page and
    /// its count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two literals here would defeat the property <c>ComposeCount</c> is written around.</b> That method
    /// takes the whole record and strips the cursor anchor itself, precisely so a term added to the read
    /// cannot be silently missed by the count — and a second, independently maintained options literal for
    /// the count would reintroduce exactly the drift it prevents. Today the two would agree (a list sets no
    /// <c>RowId</c>, no <c>LockFor</c>, no <c>Unmasked</c>); the next term added to a read's <c>WHERE</c> —
    /// a soft-delete predicate, an archive scope, F7's dynamic-entity discriminator — is where they would
    /// stop agreeing, and the count would silently describe a wider set than the page.
    /// </para>
    /// <para>
    /// <c>ReadStatementOptions.Limit</c> carries the over-fetch, not the caller's own limit, so the
    /// page can tell "more rows exist" from "the set ended here". The count ignores it, as it ignores the
    /// ordering and the anchor.
    /// </para>
    /// </remarks>
    private static ReadStatementComposer.ReadStatementOptions ReadOptions(
        AlvoQuery query, KeysetAnchor? anchor, EntitySchema? entity) =>
        new()
        {
            Filter = query.Filter,
            Anchor = anchor,
            Sort = query.Sort,
            Limit = OverFetched(query.Limit),
            Offset = query.Offset,
            Unselected = Unselected(query, entity),
        };

    /// <summary>
    /// The declared fields this read will not fetch, from the port's own rule rather than a copy of it.
    /// </summary>
    /// <remarks>
    /// <b>Delegated to <see cref="AlvoQuery.UnselectedFields"/> deliberately.</b> Which fields survive a
    /// projection is a promise <see cref="IAlvoData"/>'s returned-key-set contract makes, not a decision
    /// this driver gets to take — and the in-memory reference has to reach the same answer, or the
    /// differential suite is comparing two features rather than two implementations of one. A local copy
    /// here was the first draft, and it was a second hand-kept list of which columns the framework must
    /// return: the defect <see cref="AlvoManagedColumns"/> exists to have stopped.
    /// </remarks>
    private static IReadOnlySet<string> Unselected(AlvoQuery query, EntitySchema? entity) =>
        entity is null ? FrozenSet<string>.Empty : AlvoQuery.UnselectedFields(query, entity);

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
    /// Appends the event this write produced to the outbox, <b>on the write's own transaction</b>, so the row
    /// and its event commit together or not at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sequenced explicitly here, never hung off <c>SaveChanges</c>.</b> The idiomatic EF place for an
    /// outbox is a <c>SaveChangesInterceptor</c>, and on this data path it would silently emit on create only:
    /// an update is an <c>ExecuteUpdate</c> and a delete an <c>ExecuteDelete</c> over the policy-carrying root,
    /// and neither goes anywhere near the change tracker, so neither fires an interceptor
    /// (<c>docs/architecture/data-path.md</c>). Those are the two operations that most need an event.
    /// </para>
    /// <para>
    /// <b>Emitted last at every site, after the write's own re-read has succeeded</b>, so an event never
    /// describes a row the write did not produce — and inside the transaction, so a refused write leaves no
    /// event behind. The one exception to "last" is the idempotent create, for the reason
    /// <see cref="RecordedCreateAsync"/> records.
    /// </para>
    /// </remarks>
    private Task EmitAsync(
        AlvoDataContext db, IDbContextTransaction transaction, EntitySchema schema, OutboxOperation operation,
        AlvoContext context, DateTimeOffset now, AlvoRecord? postImage, AlvoRecord? preImage,
        CancellationToken cancellationToken) =>
        OutboxTable.InsertAsync(
            db.Database.GetDbConnection(),
            transaction.GetDbTransaction(),
            _outboxTable,
            OutboxEventFactory.For(schema, operation, context, now, postImage, preImage),
            cancellationToken);

    /// <summary>
    /// Ensures the outbox table exists, once per process and <b>before</b> the write transaction begins.
    /// </summary>
    /// <remarks>
    /// The same arrangement — and the same reasoning — as <see cref="EnsureIdempotencyTableAsync"/>: nothing
    /// calls <see cref="SystemSchemaInitializer"/> on the data path, so a host whose schema never came through
    /// a descriptor apply would otherwise have no outbox exactly when every write needs one. Outside the
    /// transaction, because inside it the DDL is a serialization point that hides the row-level control the
    /// concurrency actually rests on, and because a memo about a statement that later rolled back would be a
    /// lie. Unlike the idempotency table, <em>every</em> write reaches this one.
    /// </remarks>
    private async Task EnsureOutboxTableAsync(AlvoDataContext db, CancellationToken cancellationToken)
    {
        if (_outboxTableEnsured)
        {
            return;
        }

        var connection = db.Database.GetDbConnection();
        await RelationalSqlBatch.OpenAsync(connection, cancellationToken);
        await OutboxTable.EnsureAsync(connection, _outboxTable, cancellationToken);
        _outboxTableEnsured = true;
    }

    /// <inheritdoc cref="EnsureOutboxTableAsync"/>
    private volatile bool _outboxTableEnsured;

    /// <summary>
    /// A stored row as the framework itself sees it, with no <c>hidden</c> mask applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two callers need this, and for the same reason: a policy verdict reached over a masked pre-image would
    /// be reached over <see langword="null"/>s, and an event carrying a masked post-image would report every
    /// masked field as changed on every update. So the write path's own re-reads ask for the unmasked row
    /// (<c>ReadStatementComposer</c>'s <c>Unmasked</c> option) and the mask is applied here, in memory, to what
    /// the caller is <em>returned</em> — which is the gate <see cref="RecordMaterializer"/> describes as the
    /// one that still holds when the null projection was never applied.
    /// </para>
    /// <para>
    /// The cost is stated rather than hidden: on the write path a masked value does leave the table, into this
    /// process and into the event. That is decision D7 of the event backbone — <c>hidden</c> is a per-caller
    /// read mask, not a data classification — and its consequence for deliveries is tracked as issue #152. It
    /// does not widen what a <em>caller</em> can read: every response is still built through
    /// <see cref="RecordMaterializer.ToRecord"/> with that caller's own hidden set.
    /// </para>
    /// </remarks>
    /// <param name="row">The stored row, as the re-read returned it.</param>
    private static AlvoRecord Unmasked(Dictionary<string, object> row) =>
        RecordMaterializer.ToRecord(row, _noMask, FrozenSet<string>.Empty);

    /// <summary>
    /// The empty mask: a row a policy decision is <em>reached over</em> is never masked, only a row this data
    /// path <em>returns</em> is. A masked field read as <see langword="null"/> would silently change what a
    /// rule referencing it decides.
    /// </summary>
    private static readonly IReadOnlySet<string> _noMask = new HashSet<string>(StringComparer.Ordinal);

    /// <inheritdoc cref="AlvoDataContext.UnmappedEntityMessage"/>
    private const string UnknownEntityMessage = AlvoDataContext.UnmappedEntityMessage;
}
