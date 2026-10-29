using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>The one <see cref="IAlvoSessions"/>: a thin binding over the registered <see cref="IAlvoData"/>.</summary>
/// <param name="data">The data port every session calls.</param>
/// <param name="schemas">The applied schema, which a typed write reads its store-maintained fields from (Ruling 52).</param>
internal sealed class AlvoSessions(IAlvoData data, ISchemaRegistry? schemas = null) : IAlvoSessions
{
    /// <inheritdoc/>
    public IAlvoSession For(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new AlvoSession(data, context, schemas);
    }
}

/// <summary>One caller's view of <see cref="IAlvoData"/>; every call passes the bound context and no idempotency key.</summary>
internal sealed class AlvoSession(IAlvoData data, AlvoContext context, ISchemaRegistry? schemas) : IAlvoSession, IAlvoSessionSchema
{
    public AlvoContext Context => context;

    public EntitySchema? EntityOf(string entity) =>
        schemas?.GetSchema().Entities.FirstOrDefault(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal));

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
