using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// A reference <see cref="IAlvoData"/> backed by an in-process dictionary of rows. Not a
/// shortcut: policy is enforced <em>inside</em> this implementation exactly as a real provider
/// must enforce it — <see cref="IPolicyEngine.Resolve"/> first, then every resolved predicate
/// (<c>USING</c>, <c>WITH CHECK</c>, the synthesized tenant scope) evaluated per row through
/// <see cref="IPredicateEvaluator"/> — the same published evaluator a real provider's in-transaction
/// paths use — rather than a second, independently written evaluator.
/// </summary>
/// <remarks>
/// For a store that <em>is</em> memory, evaluating a predicate once per row through
/// <see cref="IPredicateEvaluator"/> <b>is</b> the <c>WHERE</c> clause a real provider pushes into
/// SQL — this class does not load every row and filter afterwards in some separate, redundant
/// pass. Do not read this as license to post-filter a real database query in memory: a real
/// provider must render <c>USING</c>/<c>WITH CHECK</c> into SQL and let the database apply it,
/// never fetch unfiltered rows and filter them in the application tier. This class also applies a
/// caller's own <see cref="AlvoQuery.Filter"/> in addition to the resolved policy predicate, never
/// instead of it, so a caller-supplied filter can only narrow an already-visible result, never
/// widen it.
/// </remarks>
public sealed class InMemoryAlvoData : IAlvoData
{
    private const string IdField = "id";
    private const string TenantIdField = "tenant_id";

    private readonly IPolicyEngine _policy;
    private readonly IPredicateEvaluator _evaluator;
    private readonly SchemaModel _schema;
    private readonly Dictionary<string, List<AlvoRecord>> _rows = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Initializes a new instance of the <see cref="InMemoryAlvoData"/> class.</summary>
    /// <param name="policy">Resolves the enforceable policy for every operation.</param>
    /// <param name="evaluator">Evaluates every resolved predicate against a row.</param>
    /// <param name="schema">
    /// The schema this store's entities are shaped by — used to reject a payload key naming a
    /// field the entity does not declare, so this fake and a real provider (which would reject the
    /// same payload as an unknown-column SQL error) agree.
    /// </param>
    public InMemoryAlvoData(IPolicyEngine policy, IPredicateEvaluator evaluator, SchemaModel schema)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(schema);
        _policy = policy;
        _evaluator = evaluator;
        _schema = schema;
    }

    /// <summary>Seeds an entity with initial rows, bypassing policy.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="rows">The rows to insert.</param>
    public void Seed(string entity, params AlvoRecord[] rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(rows);

        lock (_gate)
        {
            RowsForLocked(entity).AddRange(rows);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AlvoRecord>> QueryAsync(AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var decision = _policy.Resolve(query.Entity, DataOperation.List, context);
        if (decision.IsDenied)
        {
            throw Denied(decision);
        }

        AlvoFilter.EnsureWithinDepthLimit(query.Filter);
        EnsureQueryFieldsAvailable(query, decision);

        List<AlvoRecord> snapshot;
        lock (_gate)
        {
            snapshot = [.. RowsForLocked(query.Entity)];
        }

        var visible = snapshot
            .Where(row => IsVisible(row, decision, context))
            .Where(row => AlvoFilterEvaluator.Matches(query.Filter, row));
        var ordered = ApplySort(visible, query.Sort);
        var paged = ApplyPaging(ordered, query.Limit, query.After);

        IReadOnlyList<AlvoRecord> result = [.. paged.Select(row => Mask(row, decision.HiddenFields))];
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<AlvoRecord?> GetAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var decision = _policy.Resolve(entity, DataOperation.Get, context);
        if (decision.IsDenied)
        {
            throw Denied(decision);
        }

        var row = FindVisible(entity, id, decision, context);
        AlvoRecord? result = row is null ? null : Mask(row, decision.HiddenFields);
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var decision = _policy.Resolve(entity, DataOperation.Create, context);
        if (decision.IsDenied)
        {
            throw Denied(decision);
        }

        EnsureFieldsDeclared(entity, values);
        EnsureNotWriting(values, IdField, "is assigned by the store and cannot be supplied on create");
        EnsureNoReadOnlyWrite(values, decision.ReadOnlyFields);

        var candidate = new Dictionary<string, object?>(values, StringComparer.Ordinal) { [IdField] = Guid.NewGuid() };
        var postImage = new AlvoRecord(candidate);
        EnsureWriteAllowed(decision, postImage, previous: null, context);

        lock (_gate)
        {
            RowsForLocked(entity).Add(postImage);
        }

        return Task.FromResult(Mask(postImage, decision.HiddenFields));
    }

    /// <inheritdoc/>
    public Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var decision = _policy.Resolve(entity, DataOperation.Update, context);
        if (decision.IsDenied)
        {
            throw Denied(decision);
        }

        EnsureFieldsDeclared(entity, values);
        EnsureNotWriting(values, IdField, "is assigned once at creation and can never be rewritten");
        EnsureNotWriting(values, TenantIdField, "is fixed at creation and a row can never move to another tenant");
        EnsureNoReadOnlyWrite(values, decision.ReadOnlyFields);

        lock (_gate)
        {
            var list = RowsForLocked(entity);
            var index = list.FindIndex(row => IsRow(row, id));
            var stored = index >= 0 ? list[index] : null;
            if (stored is null || !IsVisible(stored, decision, context))
            {
                throw new AlvoRecordNotFoundException();
            }

            var merged = Merge(stored, values);
            EnsureWriteAllowed(decision, merged, stored, context);

            list[index] = merged;
            return Task.FromResult(Mask(merged, decision.HiddenFields));
        }
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var decision = _policy.Resolve(entity, DataOperation.Delete, context);
        if (decision.IsDenied)
        {
            throw Denied(decision);
        }

        lock (_gate)
        {
            var list = RowsForLocked(entity);
            var index = list.FindIndex(row => IsRow(row, id));
            var stored = index >= 0 ? list[index] : null;
            if (stored is null || !IsVisible(stored, decision, context))
            {
                throw new AlvoRecordNotFoundException();
            }

            list.RemoveAt(index);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Rejects a filter or sort key naming a field this caller may not read — either because the mask
    /// hides it, or because the entity's schema never declared it — <b>before</b> any row is touched.
    /// Filtering, sorting and paging all happen over the raw row and masking is applied only on the way
    /// out, so a filter over a <c>hidden</c> field would let a caller binary-search a value they may
    /// never read (one comparison per request) and a sort over one would disclose its ordering across
    /// the whole page — neither of which the response body ever shows. An undeclared name is refused
    /// here because this is the seam where a real backend interpolates it into <c>WHERE</c>/
    /// <c>ORDER BY</c> as an identifier, which has no bind-parameter form. Masks fail closed, so the
    /// query is refused rather than silently answered without the offending term.
    /// </summary>
    private void EnsureQueryFieldsAvailable(AlvoQuery query, PolicyDecision decision)
    {
        var declared = DeclaredFields(query.Entity);
        foreach (var field in QueryFields(query))
        {
            if (decision.HiddenFields.Contains(field) || !declared.Contains(field))
            {
                throw new AlvoAuthorizationException(UnavailableQueryFieldMessage);
            }
        }
    }

    /// <summary>
    /// The entity's declared field names. An entity this store's schema does not know yields an empty
    /// set, so every name fails closed rather than being waved through unchecked.
    /// </summary>
    private HashSet<string> DeclaredFields(string entity)
    {
        var entitySchema = FindEntity(entity);
        return entitySchema is null ? new HashSet<string>(StringComparer.Ordinal) : DeclaredFieldsOf(entitySchema);
    }

    private EntitySchema? FindEntity(string entity) =>
        _schema.Entities.FirstOrDefault(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal));

    private static HashSet<string> DeclaredFieldsOf(EntitySchema entity) =>
        entity.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> QueryFields(AlvoQuery query) =>
        AlvoFilter.ReferencedFields(query.Filter).Concat(query.Sort.Select(sort => sort.Field));

    /// <summary>
    /// One message for every refused query field, naming neither the field nor why it is unavailable:
    /// a caller must not be able to tell "this field exists but is hidden from you" from "this field
    /// does not exist", and the name itself is caller-supplied text this port will not echo.
    /// </summary>
    private const string UnavailableQueryFieldMessage = "The query references a field that is not available to this caller.";

    private AlvoRecord? FindVisible(string entity, Guid id, PolicyDecision decision, AlvoContext context)
    {
        lock (_gate)
        {
            var stored = RowsForLocked(entity).Find(row => IsRow(row, id));
            return stored is not null && IsVisible(stored, decision, context) ? stored : null;
        }
    }

    private List<AlvoRecord> RowsForLocked(string entity)
    {
        if (!_rows.TryGetValue(entity, out var list))
        {
            list = [];
            _rows[entity] = list;
        }

        return list;
    }

    private static bool IsRow(AlvoRecord row, Guid id) => row["id"] is Guid rowId && rowId == id;

    /// <summary>
    /// A row is visible when it satisfies both the operation's <c>USING</c> predicate and the
    /// entity's synthesized tenant scope (either may be <see langword="null"/> — <c>create</c> has
    /// no <c>USING</c>, a global entity has no tenant scope).
    /// </summary>
    private bool IsVisible(AlvoRecord row, PolicyDecision decision, AlvoContext context) =>
        (decision.Using is null || _evaluator.Evaluate(decision.Using, row, previous: null, context))
        && (decision.TenantScope is null || _evaluator.Evaluate(decision.TenantScope, row, previous: null, context));

    /// <summary>
    /// Evaluates <c>WITH CHECK</c> and the tenant scope over the complete post-image (never the
    /// payload alone), throwing if either predicate rejects the candidate row. Evaluating
    /// <see cref="PolicyDecision.TenantScope"/> here — not only on the read/visibility side — is
    /// what stops a caller from writing (creating <em>or</em> updating) a row into a tenant other
    /// than its own; <see cref="AlvoDataAdversarialTests"/>'s tenant-write facts pin this down.
    /// </summary>
    private void EnsureWriteAllowed(PolicyDecision decision, AlvoRecord postImage, AlvoRecord? previous, AlvoContext context)
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

    private static void EnsureNoReadOnlyWrite(IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> readOnlyFields)
    {
        foreach (var field in values.Keys)
        {
            if (readOnlyFields.Contains(field))
            {
                throw new AlvoAuthorizationException($"Field '{field}' is read-only and cannot be written.");
            }
        }
    }

    /// <summary>
    /// Rejects a payload that names a framework-managed column (<c>id</c>, <c>tenant_id</c>).
    /// Neither is ever a descriptor-declared field, so neither can ever appear in
    /// <see cref="PolicyDecision.ReadOnlyFields"/> — <see cref="EnsureNoReadOnlyWrite"/> alone would
    /// silently let a payload rewrite either one, corrupting row identity (two rows sharing one
    /// <c>id</c>) or moving a row to another tenant with no rule ever consulted.
    /// </summary>
    private static void EnsureNotWriting(IReadOnlyDictionary<string, object?> values, string field, string reason)
    {
        if (values.ContainsKey(field))
        {
            throw new AlvoAuthorizationException($"Field '{field}' {reason}.");
        }
    }

    /// <summary>
    /// Rejects a payload key the entity's schema does not declare — the in-memory equivalent of the
    /// unknown-column SQL error a real provider would raise. Full payload validation (types,
    /// required fields, formats) is a PR3 concern layered above this port; this is only the part a
    /// data port itself must refuse, so the fake and a real provider agree on what "not a field at
    /// all" means.
    /// </summary>
    /// <remarks>
    /// Refuses on the port's own documented failure contract — <see cref="AlvoAuthorizationException"/>,
    /// the same class of refusal every other unwritable-field rejection raises — and names neither the
    /// entity nor the offending key: the key is caller-supplied text this port will not echo, and a
    /// message naming both would answer "does this entity have a field called X?" one request at a
    /// time. An entity <see cref="_schema"/> does not know refuses every key rather than skipping the
    /// check: an inconsistency between the catalog and this store's schema must not be the one path on
    /// which an unvalidated payload reaches the rows.
    /// </remarks>
    private void EnsureFieldsDeclared(string entity, IReadOnlyDictionary<string, object?> values)
    {
        var entitySchema = FindEntity(entity)
            ?? throw new AlvoAuthorizationException(UndeclaredPayloadFieldMessage);

        var declared = DeclaredFieldsOf(entitySchema);
        foreach (var field in values.Keys)
        {
            if (!declared.Contains(field))
            {
                throw new AlvoAuthorizationException(UndeclaredPayloadFieldMessage);
            }
        }
    }

    private const string UndeclaredPayloadFieldMessage = "The payload names a field that is not writable on this entity.";

    private static AlvoRecord Merge(AlvoRecord stored, IReadOnlyDictionary<string, object?> values)
    {
        var merged = stored;
        foreach (var (field, value) in values)
        {
            merged = merged.With(field, value);
        }

        return merged;
    }

    private static AlvoRecord Mask(AlvoRecord record, IReadOnlySet<string> hiddenFields)
    {
        if (hiddenFields.Count == 0)
        {
            return record;
        }

        var visible = record.Values
            .Where(pair => !hiddenFields.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new AlvoRecord(visible);
    }

    /// <summary>
    /// Applies every sort key in order via a stable <c>OrderBy</c>/<c>ThenBy</c> chain, seeded with a
    /// no-op ordering so the loop can always call <c>ThenBy</c> uniformly. With no sort keys, falls
    /// back to ordering by <c>id</c>'s string form so paging over an unsorted query is at least
    /// deterministic — this is a fallback for this in-memory reference only, not a contract: no
    /// real engine orders a <c>uuid</c> column by its string representation, so a caller that cares
    /// about order must supply an explicit <see cref="AlvoQuery.Sort"/>.
    /// </summary>
    private static IEnumerable<AlvoRecord> ApplySort(IEnumerable<AlvoRecord> rows, IReadOnlyList<AlvoSort> sort)
    {
        if (sort.Count == 0)
        {
            return rows.OrderBy(row => row["id"]?.ToString(), StringComparer.Ordinal);
        }

        var ordered = rows.OrderBy(static _ => 0);
        foreach (var key in sort)
        {
            ordered = ordered.ThenBy(static row => row, new SortKeyComparer(key));
        }

        return ordered;
    }

    /// <summary>
    /// Compares two rows by one <see cref="AlvoSort"/> key: <see langword="null"/> sorts per
    /// <see cref="AlvoSort.Nulls"/> regardless of direction; two non-null, same-typed
    /// <see cref="IComparable"/> values compare directly; anything else (mismatched types, a
    /// non-comparable value) falls back to an ordinal string comparison so sorting a field of
    /// heterogeneous or exotic values never throws.
    /// </summary>
    private sealed class SortKeyComparer(AlvoSort key) : IComparer<AlvoRecord>
    {
        public int Compare(AlvoRecord? x, AlvoRecord? y)
        {
            var left = x?[key.Field];
            var right = y?[key.Field];
            if (left is null || right is null)
            {
                return CompareWithNull(left, right);
            }

            var comparison = left is IComparable comparable && left.GetType() == right.GetType()
                ? comparable.CompareTo(right)
                : string.CompareOrdinal(left.ToString(), right.ToString());
            return key.Descending ? -comparison : comparison;
        }

        private int CompareWithNull(object? left, object? right)
        {
            if (left is null && right is null)
            {
                return 0;
            }

            var nullIsFirst = key.Nulls == AlvoNullPlacement.First;
            return left is null
                ? (nullIsFirst ? -1 : 1)
                : (nullIsFirst ? 1 : -1);
        }
    }

    private static IEnumerable<AlvoRecord> ApplyPaging(IEnumerable<AlvoRecord> rows, int? limit, string? after)
    {
        var remaining = after is null ? rows : SkipUntilAfter(rows, after);
        return limit is int max ? remaining.Take(max) : remaining;
    }

    /// <summary>
    /// Skips every row up to and including the one whose cursor matches <paramref name="after"/>.
    /// A cursor this store never issued (stale, forged, or from a different store) matches nothing,
    /// so the result reads as an empty final page rather than throwing or silently restarting from
    /// the beginning.
    /// </summary>
    private static IEnumerable<AlvoRecord> SkipUntilAfter(IEnumerable<AlvoRecord> rows, string after)
    {
        var seenCursor = false;
        foreach (var row in rows)
        {
            if (seenCursor)
            {
                yield return row;
                continue;
            }

            if (string.Equals(Cursor(row), after, StringComparison.Ordinal))
            {
                seenCursor = true;
            }
        }
    }

    /// <summary>
    /// The opaque keyset cursor for a row: this in-memory reference encodes it as the row's
    /// <c>id</c>. A real provider is free to choose its own encoding — <see cref="AlvoQuery.After"/>
    /// only requires that a cursor a provider returned is accepted back by that same provider.
    /// </summary>
    private static string Cursor(AlvoRecord row) => row["id"]?.ToString() ?? string.Empty;

    /// <summary>
    /// <see cref="PolicyDecision.DenyReason"/> is already designed, at the policy layer, never to
    /// name the entity or echo caller-supplied text (see <c>PolicyEngine</c>'s own docs) — the
    /// tenant guard's reason deliberately names "tenant" specifically, a conscious, narrow oracle an
    /// operator needs and a test depends on. This port passes that reason through verbatim rather
    /// than mapping it to one more-generic message, trusting the policy layer's own tradeoff instead
    /// of re-deciding it here.
    /// </summary>
    private static AlvoAuthorizationException Denied(PolicyDecision decision) =>
        new(decision.DenyReason ?? "The operation was not authorized.");
}
