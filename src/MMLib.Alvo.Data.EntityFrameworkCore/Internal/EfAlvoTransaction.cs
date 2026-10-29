namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>One unit's <see cref="IAlvoTransaction"/>: every call goes through the unit as <see cref="Context"/>.</summary>
/// <param name="port">The data port the calls run through.</param>
/// <param name="unit">The unit they run in.</param>
/// <param name="context">The caller they run as.</param>
internal sealed class EfAlvoTransaction(EfAlvoData port, WriteUnit unit, AlvoContext context) : IAlvoTransaction
{
    /// <inheritdoc/>
    public AlvoContext Context => context;

    /// <inheritdoc/>
    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) =>
        port.QueryInUnitAsync(unit, query, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) =>
        port.GetInUnitAsync(unit, entity, id, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        port.CreateInUnitAsync(unit, entity, values, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default) =>
        port.UpdateInUnitAsync(unit, entity, id, values, context, precondition, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoReplaceResult> ReplaceAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default) =>
        port.ReplaceInUnitAsync(unit, entity, id, values, context, precondition, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteAsync(string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        port.DeleteInUnitAsync(unit, entity, id, context, precondition, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoBatchResult> CreateManyAsync(
        string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
        port.CreateManyInUnitAsync(unit, entity, rows, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default) =>
        port.UpdateManyInUnitAsync(unit, entity, rows, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        port.DeleteManyInUnitAsync(unit, entity, ids, context, cancellationToken);

    /// <inheritdoc/>
    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) =>
        port.PublishInUnitAsync(unit, type, subject, data, context, cancellationToken);

    /// <inheritdoc/>
    public IAlvoTransaction AsSystem() =>
        new EfAlvoTransaction(port, unit, AlvoContext.System(context.Tenant) with { Provenance = context.Provenance });

    /// <summary>The same unit as the same caller, one hop deeper — what a write made under another write's stage acts as.</summary>
    /// <exception cref="AlvoChainDepthExceededException">One hop deeper is past the limit; the unit is poisoned.</exception>
    internal EfAlvoTransaction Deeper() => new(port, unit, port.Deeper(unit, context));
}
