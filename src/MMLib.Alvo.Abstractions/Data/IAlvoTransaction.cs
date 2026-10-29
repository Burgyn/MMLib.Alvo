namespace MMLib.Alvo.Data;

/// <summary>
/// The data access of one transaction unit: every call runs as <see cref="IAlvoSession.Context"/>, through the same rules,
/// tenancy and chain-depth checks as <see cref="IAlvoData"/>, inside the unit's one database transaction.
/// </summary>
/// <remarks>
/// <para>
/// Unlike a session from <see cref="IAlvoSessions"/>, nothing commits on its own: the unit commits when its delegate
/// returns, and rolls back if it throws. Reads see the unit's own writes.
/// </para>
/// <para>
/// <b>A refused or failed call does not end the unit.</b> Each call runs under its own savepoint, so an authorization
/// refusal, a constraint violation or a failed precondition undoes that call alone, on every engine; catch it and go on.
/// <b>A few failures end it</b> — the unit's timeout, its chain-depth limit, a cancelled call, two calls at once, or a call
/// through another data port while it is open: catching one of those cannot make the unit commit.
/// </para>
/// <para>
/// One call at a time: await each call before making the next. A call the delegate starts and does not await before it
/// returns is not part of the unit — it is refused, or runs after the commit decided without it. No call takes an
/// idempotency key — a unit is never replayed. Alvo implements this interface and may add members to it; it is not meant
/// to be implemented by host code.
/// </para>
/// </remarks>
public interface IAlvoTransaction : IAlvoSession
{
    /// <summary>Creates every row of a batch as one all-or-nothing write inside the unit.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="rows">The rows to create.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The written rows, or every row's refusal and nothing written.</returns>
    Task<AlvoBatchResult> CreateManyAsync(
        string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default);

    /// <summary>Updates every row of a batch as one all-or-nothing write inside the unit.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="rows">The rows and the fields to change on each.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The written rows, or every row's refusal and nothing written.</returns>
    Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default);

    /// <summary>Deletes every row of a batch as one all-or-nothing write inside the unit.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="ids">The rows to delete.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>How many rows were deleted, or every row's refusal and nothing deleted.</returns>
    Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>Publishes one custom application event that commits with the unit, or not at all.</summary>
    /// <param name="type">The event's name, as <c>IAlvoEvents.PublishAsync</c> accepts it.</param>
    /// <param name="subject">What the event is about, and its partition key.</param>
    /// <param name="data">The payload — flat scalars — or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the event is written into the unit.</returns>
    /// <exception cref="ArgumentException">The name is reserved or malformed, or the payload carries a value the envelope cannot express.</exception>
    Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default);

    /// <summary>The same unit, acting as the system in the caller's tenant, with the caller's provenance.</summary>
    /// <returns>A view whose calls run as <c>AlvoContext.System(tenant)</c> — never in another tenant.</returns>
    IAlvoTransaction AsSystem();
}
