using MMLib.Alvo.Data;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>The one <see cref="IAlvoSessions"/>: a thin binding over the registered <see cref="IAlvoData"/>.</summary>
internal sealed class AlvoSessions(IAlvoData data) : IAlvoSessions
{
    /// <inheritdoc/>
    public IAlvoSession For(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new AlvoSession(data, context);
    }
}

/// <summary>One caller's view of <see cref="IAlvoData"/>; every call passes the bound context and no idempotency key.</summary>
internal sealed class AlvoSession(IAlvoData data, AlvoContext context) : IAlvoSession
{
    public AlvoContext Context => context;

    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) =>
        data.QueryAsync(query, context, cancellationToken);

    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) =>
        data.GetAsync(entity, id, context, cancellationToken);

    public Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        data.CreateAsync(entity, values, context, idempotency: null, cancellationToken);

    public Task<AlvoRecord> UpdateAsync(
        string entity,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default) =>
        data.UpdateAsync(entity, id, values, context, precondition, idempotency: null, cancellationToken);

    public Task<AlvoReplaceResult> ReplaceAsync(
        string entity,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default) =>
        data.ReplaceAsync(entity, id, values, context, precondition, idempotency: null, cancellationToken);

    public Task DeleteAsync(
        string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        data.DeleteAsync(entity, id, context, precondition, idempotency: null, cancellationToken);
}
