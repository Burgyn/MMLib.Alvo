namespace MMLib.Alvo.Events;

/// <summary>
/// The one member the descriptor-action sweep needs beyond <see cref="IDeliveryStore"/> (issue 480): ends the unfinished
/// deliveries of a subscriber the serving catalog no longer binds, and records that in the operations log, in one
/// transaction. Implemented by the relational driver's delivery store; the core reaches it through <c>InternalsVisibleTo</c>.
/// </summary>
/// <remarks>
/// <b>Internal, not a port member.</b> Only the core decides that a descriptor action is superseded, and a store that does
/// not implement it still ends the work through the public <see cref="IDeliveryStore.CancelPendingAsync"/> — it only goes
/// unrecorded in the log. Widening the port would make every provider author implement a member only Alvo calls.
/// </remarks>
internal interface ISupersededDeliveryStore
{
    /// <summary>
    /// Cancels <paramref name="subscriber"/>'s unfinished deliveries — pending, retrying and running, whose late answer then
    /// changes nothing — recording <see cref="Superseded"/> as each one's error type, and, when it cancelled any, records
    /// <paramref name="entry"/> with how many, in the same transaction. Serialized against the subscriber's claims, as a pause is.
    /// </summary>
    /// <param name="subscriber">The subscriber id.</param>
    /// <param name="entry">The log row to write when a delivery was cancelled; its <see cref="DeliveryOperationEntry.Affected"/> is set by the store.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>How many deliveries were cancelled; zero records nothing.</returns>
    /// <exception cref="InvalidOperationException">The log refused <paramref name="entry"/>; nothing is cancelled.</exception>
    Task<int> CancelSupersededAsync(string subscriber, DeliveryOperationEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The error type a superseded delivery records: <c>superseded</c>.</summary>
    internal const string Superseded = "superseded";
}
