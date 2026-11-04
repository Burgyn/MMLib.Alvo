using MMLib.Alvo.Data;
using System.Diagnostics;

namespace MMLib.Alvo.TestHost.Internal;

/// <summary>
/// The test host's <see cref="AlvoTestHost.Data"/>: the host's own port, each call inside a recorded root activity, so the write
/// hooks a call runs are this host's runs for <see cref="AlvoHookRecorder"/> (Ruling 27).
/// </summary>
internal sealed class TrackedData(IAlvoData inner, Func<Activity?> track) : IAlvoData
{
    public async Task<AlvoPage> QueryAsync(AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.QueryAsync(query, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoRecord?> GetAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.GetAsync(entity, id, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.CreateAsync(entity, values, context, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoPrecondition? precondition = null,
        AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.UpdateAsync(entity, id, values, context, precondition, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoReplaceResult> ReplaceAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoPrecondition? precondition = null,
        AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.ReplaceAsync(entity, id, values, context, precondition, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        await inner.DeleteAsync(entity, id, context, precondition, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoBatchResult> CreateManyAsync(
        string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.CreateManyAsync(entity, rows, context, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoBatchResult> UpdateManyAsync(
        string entity, IReadOnlyList<AlvoRowPatch> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.UpdateManyAsync(entity, rows, context, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoBatchResult> DeleteManyAsync(
        string entity, IReadOnlyList<Guid> ids, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.DeleteManyAsync(entity, ids, context, idempotency, cancellationToken).ConfigureAwait(false);
    }
}
