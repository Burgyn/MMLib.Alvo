using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// A reference <see cref="IAlvoData"/> backed by an in-process dictionary of rows. Not a
/// shortcut: policy is enforced <em>inside</em> this implementation exactly as a real provider
/// must enforce it — <see cref="IPolicyEngine.Resolve"/> first, then every resolved predicate
/// (<c>USING</c>, <c>WITH CHECK</c>, the synthesized tenant scope) evaluated per row through
/// <c>MMLib.Alvo.Expressions.Internal.CelInterpreter</c> — the exact backend a real provider's
/// <c>WITH CHECK</c> evaluates candidate rows with — rather than a second, independently written
/// evaluator.
/// </summary>
/// <remarks>
/// For a store that <em>is</em> memory, evaluating a predicate once per row through the CEL
/// interpreter <b>is</b> the <c>WHERE</c> clause a real provider pushes into SQL — this class does
/// not load every row and filter afterwards in some separate, redundant pass. Do not read this as
/// license to post-filter a real database query in memory: a real provider must render <c>USING</c>/
/// <c>WITH CHECK</c> into SQL and let the database apply it, never fetch unfiltered rows and filter
/// them in the application tier. This class also applies a caller's own <see cref="AlvoQuery.Filter"/>
/// in addition to the resolved policy predicate, never instead of it, so a caller-supplied filter can
/// only narrow an already-visible result, never widen it.
/// </remarks>
public sealed class InMemoryAlvoData : IAlvoData
{
    private readonly IPolicyEngine _policy;
    private readonly SchemaModel _schema;
    private readonly Dictionary<string, List<AlvoRecord>> _rows = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Initializes a new instance of the <see cref="InMemoryAlvoData"/> class.</summary>
    /// <param name="policy">Resolves the enforceable policy for every operation.</param>
    /// <param name="schema">The schema this store's entities are shaped by.</param>
    public InMemoryAlvoData(IPolicyEngine policy, SchemaModel schema)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(schema);
        _policy = policy;
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

        EnsureNoReadOnlyWrite(values, decision.ReadOnlyFields);

        var candidate = new Dictionary<string, object?>(values, StringComparer.Ordinal) { ["id"] = Guid.NewGuid() };
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
    private static bool IsVisible(AlvoRecord row, PolicyDecision decision, AlvoContext context) =>
        (decision.Using is null || CelInterpreter.EvaluatePredicate(decision.Using, row, previous: null, context))
        && (decision.TenantScope is null || CelInterpreter.EvaluatePredicate(decision.TenantScope, row, previous: null, context));

    /// <summary>
    /// Evaluates <c>WITH CHECK</c> and the tenant scope over the complete post-image (never the
    /// payload alone), throwing if either predicate rejects the candidate row.
    /// </summary>
    private static void EnsureWriteAllowed(PolicyDecision decision, AlvoRecord postImage, AlvoRecord? previous, AlvoContext context)
    {
        var passesCheck = decision.WithCheck is null
            || CelInterpreter.EvaluatePredicate(decision.WithCheck, postImage, previous, context);
        var passesTenantScope = decision.TenantScope is null
            || CelInterpreter.EvaluatePredicate(decision.TenantScope, postImage, previous, context);

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
    /// back to ordering by <c>id</c> so paging over an unsorted query is still deterministic.
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

    private static AlvoAuthorizationException Denied(PolicyDecision decision) =>
        new(decision.DenyReason ?? "The operation was not authorized.");
}
