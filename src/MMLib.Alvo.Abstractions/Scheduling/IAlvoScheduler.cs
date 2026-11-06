namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Durable, keyed timers: arm one to fire a custom event at a given time, move it by arming the same key again, or cancel
/// it. A fired timer is an event, delivered to its handlers like any other.
/// </summary>
/// <remarks>
/// <para>
/// <b>A key is unique within its tenant.</b> Arming a key that is already armed replaces it: the timer fires once, at the
/// latest time it was given, even when the earlier time's firing is already in flight.
/// </para>
/// <para>
/// <b>The tenant</b> is the <c>tenant</c> argument, else the tenant of the transaction unit open on the calling flow. It is
/// required when the schema has a tenant-scoped entity and no unit is open; inside a unit a different tenant is refused.
/// </para>
/// <para>
/// <b>Inside a transaction unit the timer is written in the unit's transaction</b>, so a unit that rolls back leaves no
/// timer behind. The fired event continues the flow that armed it: its correlation, its causation and its chain depth,
/// so a timer that re-arms itself stops at the chain-depth limit like any other loop.
/// </para>
/// </remarks>
public interface IAlvoScheduler
{
    /// <summary>Arms the timer <paramref name="key"/> to fire <paramref name="eventType"/> at <paramref name="at"/>, replacing any timer of that key.</summary>
    /// <param name="key">The timer's key: 1–200 characters, none of them a control character.</param>
    /// <param name="at">When it fires; a time already past fires on the next round.</param>
    /// <param name="eventType">The custom event type it fires; the reserved <c>alvo</c> namespace is refused.</param>
    /// <param name="data">The event's payload — a flat record of scalars — or <see langword="null"/>.</param>
    /// <param name="tenant">The tenant the timer belongs to; defaults to the open unit's tenant.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the timer is armed.</returns>
    /// <exception cref="ArgumentException">
    /// The key, event type or payload is refused, the tenant is required and missing, or it differs from the open unit's.
    /// </exception>
    /// <exception cref="Data.AlvoChainDepthExceededException">The calling flow is deeper than the chain-depth limit.</exception>
    Task ScheduleAsync(
        string key,
        DateTimeOffset at,
        string eventType,
        IReadOnlyDictionary<string, object?>? data,
        TenantId? tenant = null,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels the timer <paramref name="key"/> of the tenant, if one is armed.</summary>
    /// <param name="key">The timer's key.</param>
    /// <param name="tenant">The tenant the timer belongs to; defaults to the open unit's tenant.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when a timer was cancelled; <see langword="false"/> when none was armed.</returns>
    /// <exception cref="ArgumentException">The tenant is required and missing, or it differs from the open unit's.</exception>
    Task<bool> CancelAsync(string key, TenantId? tenant = null, CancellationToken cancellationToken = default);
}
