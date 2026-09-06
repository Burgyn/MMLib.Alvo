using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// An <see cref="IAlvoData"/> whose every member raises the port's fifth failure family — "an invariant the
/// implementation itself relies on is broken".
/// </summary>
/// <remarks>
/// It exists because that family is, by design, <b>unreachable from a well-formed request</b>: the port's
/// contract says so, which is precisely why #119's hole could sit unnoticed. Registered <em>before</em>
/// <c>AddAlvo</c>, so the provider's own <c>TryAddSingleton&lt;IAlvoData&gt;</c> leaves it in place — no
/// decoration, no reflection over service descriptors.
/// </remarks>
internal sealed class FaultingAlvoData : IAlvoData
{
    internal const string FailureMessage = "The faulting store's own invariant is broken.";

    public Task<AlvoPage> QueryAsync(AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(FailureMessage);

    public Task<AlvoRecord?> GetAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(FailureMessage);

    public Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(FailureMessage);

    public Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(FailureMessage);

    public Task DeleteAsync(string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(FailureMessage);

    public Task<AlvoBatchResult> CreateManyAsync(string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(FailureMessage);

    public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(FailureMessage);

    public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(FailureMessage);
}
