using MMLib.Alvo.Events;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The durable store behind Alvo's time: keyed timers, cron schedules and data migrations, each fired by appending its
/// event to the outbox in the <b>same transaction</b> that retires or advances it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A provider implements this port and <see cref="IOutboxStore"/> over one database</b>, because every firing is one
/// commit: the fenced delete, advance or insert and the outbox append land together or not at all. A store that appended
/// elsewhere would fire twice or never after a crash between the two.
/// </para>
/// <para>
/// <b>No member takes a bare <see cref="AlvoEvent"/>.</b> A timer fires an <see cref="AlvoCustomEvent"/>, whose only
/// factory refuses the reserved <c>alvo</c> namespace; a schedule or migration run is an
/// <see cref="AlvoFrameworkEnvelope"/>, which only Alvo can mint; a timer is a <see cref="TimerEntry"/>, which only Alvo
/// builds. So nothing resolved from the container can append a framework event or choose a timer's provenance.
/// </para>
/// <para>
/// Every instant is UTC. A claim leases what it returns; a lease that runs out makes the row claimable again, which is how
/// a crashed replica's work is recovered.
/// </para>
/// </remarks>
public interface ITimerStore
{
    /// <summary>Creates the store's tables and the outbox if they do not exist yet; idempotent.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the storage exists.</returns>
    Task EnsureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Arms <paramref name="timer"/>, replacing any timer of the same scope and key: a new generation, the new due time and
    /// message, and no lease — so a moved timer is claimable at its new time even while an older claim runs.
    /// </summary>
    /// <param name="timer">The timer.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The generation the timer was armed under; a fire must name it.</returns>
    Task<Guid> UpsertAsync(TimerEntry timer, CancellationToken cancellationToken = default);

    /// <summary>Removes the timer of <paramref name="scope"/> and <paramref name="key"/>.</summary>
    /// <param name="scope">The tenant id text, or <c>""</c> for none.</param>
    /// <param name="key">The timer's key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when a timer was removed; <see langword="false"/> when there was none.</returns>
    Task<bool> CancelAsync(string scope, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the timer of <paramref name="scope"/> and <paramref name="key"/> only while it is still armed at
    /// <paramref name="generation"/> — so retiring a claimed timer never removes one re-armed since the claim.
    /// </summary>
    /// <param name="scope">The claimed timer's scope.</param>
    /// <param name="key">The claimed timer's key.</param>
    /// <param name="generation">The generation the claim read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the timer was removed; <see langword="false"/> when it was moved, cancelled or fired since.
    /// </returns>
    Task<bool> CancelAsync(string scope, string key, Guid generation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leases up to <paramref name="batchSize"/> due timers that no unexpired lease holds, the earliest due first.
    /// </summary>
    /// <param name="batchSize">The most timers to take; at least one.</param>
    /// <param name="lease">How long the claim holds before another claimant may take a timer back.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The claimed timers, earliest due first.</returns>
    Task<IReadOnlyList<ClaimedTimer>> ClaimTimersAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires one claimed timer: deletes it only at <paramref name="generation"/> and appends <paramref name="customEvent"/> in the
    /// same transaction.
    /// </summary>
    /// <remarks>
    /// The event, its provenance included, is the caller's: Alvo's timer pump rebuilds it from
    /// <see cref="ClaimedTimer.Provenance"/> and <see cref="ClaimedTimer.Payload"/>. The member grants no more than
    /// <see cref="IOutboxStore.AppendAsync"/> already does — a custom event appended as given.
    /// </remarks>
    /// <param name="scope">The claimed timer's scope.</param>
    /// <param name="key">The claimed timer's key.</param>
    /// <param name="generation">The generation the claim read.</param>
    /// <param name="customEvent">The event the timer fires.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the timer fired; <see langword="false"/>, appending nothing, when it was moved, cancelled
    /// or already fired since the claim. A failed append throws and leaves the timer in place.
    /// </returns>
    Task<bool> FireAsync(string scope, string key, Guid generation, AlvoCustomEvent customEvent, CancellationToken cancellationToken = default);

    /// <summary>Counts the armed timers, and those of them already due.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The counts.</returns>
    Task<TimerCounts> CountTimersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the stored schedules exactly <paramref name="schedules"/>: inserts a new one, moves the next run of one whose
    /// cron or time zone changed, keeps the next run of an unchanged one, and deletes every stored id not listed — an empty
    /// list empties the store.
    /// </summary>
    /// <param name="schedules">Every schedule this deployment registers.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the schedules are synchronized.</returns>
    Task SyncSchedulesAsync(IReadOnlyList<ScheduleDefinition> schedules, CancellationToken cancellationToken = default);

    /// <summary>Leases every due schedule that no unexpired lease holds, for <paramref name="claimant"/>.</summary>
    /// <param name="claimant">Who claims; unique to this claim, and the fence its advance must name.</param>
    /// <param name="lease">How long the claim holds before another claimant may take an occurrence back.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The claimed occurrences.</returns>
    Task<IReadOnlyList<ClaimedSchedule>> ClaimSchedulesAsync(string claimant, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one claimed occurrence: moves the schedule to its next run, records the occurrence as its last run and appends
    /// the run's events, in one transaction — only while <see cref="ScheduleAdvance.Claimant"/> still holds the lease and the
    /// schedule's next run is still <see cref="ScheduleAdvance.ScheduledFor"/>.
    /// </summary>
    /// <param name="advance">The occurrence, its next run and its events.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the occurrence advanced; <see langword="false"/>, appending nothing, when the lease was
    /// taken over or the schedule moved. A failed append throws and leaves the occurrence due.
    /// </returns>
    Task<bool> AdvanceScheduleAsync(ScheduleAdvance advance, CancellationToken cancellationToken = default);

    /// <summary>Appends an out-of-band run's events — an operator's run now — in one transaction, moving no schedule.</summary>
    /// <param name="events">The run's events.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the events are appended.</returns>
    Task AppendRunAsync(IReadOnlyList<AlvoFrameworkEnvelope> events, CancellationToken cancellationToken = default);

    /// <summary>Reads every stored schedule, by id.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The schedules.</returns>
    Task<IReadOnlyList<StoredSchedule>> SchedulesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts data migration <paramref name="id"/> once: marks it started at <paramref name="revision"/> and appends its
    /// events in one transaction, or does nothing when it was started before, at any revision.
    /// </summary>
    /// <param name="id">The migration's id.</param>
    /// <param name="revision">The applied descriptor revision it starts after.</param>
    /// <param name="events">The run's events.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when this call started it; <see langword="false"/>, appending nothing, when it had started. A
    /// failed append throws and leaves it unmarked.
    /// </returns>
    Task<bool> StartMigrationAsync(string id, int revision, IReadOnlyList<AlvoFrameworkEnvelope> events, CancellationToken cancellationToken = default);

    /// <summary>Reads the ids of every started data migration, in ordinal order.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The ids.</returns>
    Task<IReadOnlyList<string>> StartedMigrationsAsync(CancellationToken cancellationToken = default);
}
