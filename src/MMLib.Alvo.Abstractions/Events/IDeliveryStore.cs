namespace MMLib.Alvo.Events;

/// <summary>One subscriber's copy of one event, as the fan-out asks the store to record it.</summary>
/// <remarks>
/// Init-only properties rather than a positional constructor, here and on every record of this port: a provider builds
/// and reads these, and a member added later must not change a constructor or a <c>Deconstruct</c> it compiled against.
/// </remarks>
public sealed record DeliveryRequest
{
    /// <summary>
    /// The dead reason of a delivery whose handler was not bound when its event was fanned out: a record of missed work,
    /// replayed explicitly, which — unlike any other dead delivery — does not hold its lane back.
    /// </summary>
    public const string UnboundReason = "unbound";

    /// <summary>Gets the outbox entry the delivery is for; its envelope stays in the outbox.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Gets the registration's id.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets the event's partition key — the lane the delivery is ordered in.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Gets the event's tenant, or <see langword="null"/> for a global row or a tenantless event.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets what runs the delivery: <c>handler</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets why the delivery is recorded <c>dead</c> instead of <c>pending</c> — a condition that threw, a chain too
    /// deep, a handler not bound — or <see langword="null"/> for a delivery to run.
    /// </summary>
    public string? DeadReason { get; init; }
}

/// <summary>What one claim asks for: whose deliveries, how many, and for how long.</summary>
public sealed record DeliveryClaim
{
    /// <summary>
    /// Gets who claims — unique to this one claim, never reused: each row records it, and an attempt is answered for only by
    /// the claim that took it.
    /// </summary>
    public required string Claimant { get; init; }

    /// <summary>
    /// Gets the subscribers whose lanes are ordered: a delivery is claimable only when no earlier delivery of its lane is
    /// unfinished or dead — a delivery dead with <see cref="DeliveryRequest.UnboundReason"/> excepted — and no earlier
    /// outbox entry of its partition key is still waiting to be fanned out.
    /// </summary>
    public required IReadOnlyCollection<string> Ordered { get; init; }

    /// <summary>Gets the subscribers that opted out of per-record order; their deliveries are claimable whatever came before.</summary>
    public IReadOnlyCollection<string> Unordered { get; init; } = [];

    /// <summary>Gets the most deliveries this claim takes; a store refuses zero or less with <see cref="ArgumentOutOfRangeException"/>.</summary>
    public required int BatchSize { get; init; }

    /// <summary>Gets how long the claim holds before another claimant may take the delivery back.</summary>
    public required TimeSpan Lease { get; init; }

    /// <summary>
    /// Gets the outbox's attempt ceiling: an earlier outbox entry that reached it will never be fanned out, so it no longer
    /// holds an ordered lane back.
    /// </summary>
    public required int OutboxMaxAttempts { get; init; }

    /// <summary>
    /// Gets the outbox's claim lease: an earlier entry under a claim younger than this is still being processed — even on
    /// its last attempt, whose claim already counted it at the ceiling — so it holds an ordered lane back until it is
    /// dispatched or its claim goes stale.
    /// </summary>
    public required TimeSpan OutboxClaimLease { get; init; }
}

/// <summary>One claimed delivery, with the envelope it is for.</summary>
public sealed record ClaimedDelivery
{
    /// <summary>Gets the delivery's own id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the event it delivers.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Gets the registration that runs it.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets its lane.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Gets how many times it has been claimed, <b>including</b> this claim.</summary>
    public required int Attempts { get; init; }

    /// <summary>Gets the stored envelope, or <see langword="null"/> when the outbox no longer holds it.</summary>
    public string? Payload { get; init; }
}

/// <summary>One finished attempt, as the claimant answers for it.</summary>
public sealed record DeliveryAttempt
{
    /// <summary>Gets the delivery as it was claimed.</summary>
    public required ClaimedDelivery Delivery { get; init; }

    /// <summary>Gets who claimed it; the store records nothing unless the row is still this claimant's, at this attempt.</summary>
    public required string Claimant { get; init; }

    /// <summary>Gets when the attempt started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets how long it ran.</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>Why an attempt failed and what happens next.</summary>
public sealed record DeliveryFailure
{
    /// <summary>Gets a type name or a fixed reason — never an exception message.</summary>
    public required string ErrorType { get; init; }

    /// <summary>Gets how long until the next attempt, or <see langword="null"/> when the failure is not worth retrying.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Gets the attempt ceiling: a failure at this attempt or later is dead whatever <see cref="RetryAfter"/> says.</summary>
    public required int MaxAttempts { get; init; }
}

/// <summary>
/// The durable per-subscriber delivery state behind event handlers: one row per (event, subscriber), claimed under a
/// lease, ordered per lane, retried, dead-lettered, and answered for only by the claimant that still holds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One database with the outbox.</b> A claim returns each delivery's envelope from the outbox it was fanned out of, so
/// an implementation serves this port and <see cref="IOutboxStore"/> over one store.
/// </para>
/// <para>
/// <b>Never a read followed by a write in one transaction</b> — the shape <see cref="IOutboxStore"/> measured to fail on
/// SQLite; a member that writes more than once opens its transaction with the write. A claim is a single conditional update
/// whose outer condition repeats the claimability test, so a second claimant blocked behind the first claims nothing
/// rather than the same rows.
/// </para>
/// <para>
/// <b>Lanes.</b> A delivery is claimable when it is pending, or retrying and due, or running with its lease expired; its
/// subscriber is one the claim names and is not paused; and — for an ordered subscriber — no delivery of the same subscriber and partition
/// key with an earlier event id is pending, running, retrying or dead — except a delivery dead with
/// <see cref="DeliveryRequest.UnboundReason"/>, a record of work missed while its handler did not fit the schema, which is replayed explicitly and does
/// not hold the lane — <b>and no outbox entry of the same partition key with an earlier id is still undispatched, either
/// below the outbox's attempt ceiling or under a claim younger than the outbox's claim lease</b>. The second half is what
/// keeps a record's order across outbox batches, a failed fan-out and a dispatcher that died between claiming an entry and
/// fanning it out: a later event's delivery cannot overtake an earlier event that has not been fanned out yet. Any other
/// dead delivery blocks its lane until it is redelivered or retired.
/// </para>
/// <para>
/// <b>Fencing.</b> <see cref="CompleteAsync"/> and <see cref="FailAsync"/> change the row only while it is still running
/// under the same claimant and the same attempt count, and answer whether they did — <see langword="false"/>, never an
/// exception, when the claim no longer holds. A late finisher whose lease was reclaimed, a second answer for one attempt,
/// and an answer for a delivery retired while it ran all change nothing. A claimant names one claim, not a process: a
/// delivery put back to pending restarts its attempt count, so the claimant is what tells a stale claim's answer from the
/// current one.
/// </para>
/// <para>
/// <b>The operator's members</b> read deliveries, put finished ones back to pending, pause a subscriber and record each of
/// those changes in the operations log in the same transaction as the change; a member that changes nothing records nothing.
/// They are abstract: a default that did nothing would report success and leave a dead delivery's lane blocked.
/// </para>
/// </remarks>
public interface IDeliveryStore
{
    /// <summary>Brings the delivery storage up; idempotent and safe from several processes at once.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the storage exists.</returns>
    Task EnsureAsync(CancellationToken cancellationToken = default);

    /// <summary>Records one delivery per request; a request whose (event, subscriber) pair already exists is skipped.</summary>
    /// <param name="requests">The deliveries one event fans out to, all recorded together or not at all.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>How many rows were new.</returns>
    Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <see cref="DeliveryClaim.BatchSize"/> claimable deliveries for the claimant — the head of each ordered
    /// lane, any due delivery of an unordered subscriber.
    /// </summary>
    /// <param name="claim">Whose deliveries, how many, for how long.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The claimed deliveries, ascending by event id and then subscriber.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="DeliveryClaim.BatchSize"/> is zero or less.</exception>
    Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default);

    /// <summary>Marks the attempt succeeded and records it, if the claimant still holds the delivery.</summary>
    /// <param name="attempt">The finished attempt.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the claim still held; <see langword="false"/> changed nothing.</returns>
    Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the attempt failed and records it, if the claimant still holds the delivery: retrying after
    /// <see cref="DeliveryFailure.RetryAfter"/>, or dead when there is none or the attempt reached the ceiling.
    /// </summary>
    /// <param name="attempt">The finished attempt.</param>
    /// <param name="failure">Why it failed and what happens next.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the claim still held; <see langword="false"/> changed nothing.</returns>
    Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default);

    /// <summary>The subscribers with work left — a delivery pending, running or retrying — in no promised order.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The subscriber ids.</returns>
    Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends a subscriber's unfinished deliveries — a running one included, whose late answer then changes nothing — as dead
    /// with <paramref name="reason"/>, so they stay inspectable.
    /// </summary>
    /// <param name="subscriber">The subscriber id.</param>
    /// <param name="reason">The reason recorded as each delivery's error type.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>How many deliveries were ended.</returns>
    Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default);

    /// <summary>Cancels a subscriber's unfinished deliveries — a running one included, whose late answer then changes nothing.</summary>
    /// <param name="subscriber">The subscriber id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>How many deliveries were cancelled.</returns>
    Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default);

    /// <summary>A page of deliveries, newest first — by delivery id — strictly after <see cref="DeliveryFilter.After"/>, without payloads.</summary>
    /// <param name="filter">Which deliveries, and how many.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>At most <see cref="DeliveryFilter.Limit"/> deliveries.</returns>
    /// <remarks>
    /// A page is a stable walk, not a snapshot. A delivery id is minted when the delivery is fanned out, in order within one
    /// process only: a delivery another replica fanned out in the same instant, or one whose fan-out committed after a page
    /// was read, can carry an id older than the cursor and is then never reached by continuing. Paging forward never
    /// repeats or skips a delivery that existed when its page was read; to see late arrivals, read again from the newest.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="DeliveryFilter.Limit"/> is zero or less, or more than 1 000.</exception>
    Task<IReadOnlyList<StoredDelivery>> ListAsync(DeliveryFilter filter, CancellationToken cancellationToken = default);

    /// <summary>One delivery, with its stored envelope.</summary>
    /// <param name="id">The delivery id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The delivery, or <see langword="null"/> when no fan-out recorded it.</returns>
    Task<StoredDelivery?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>A delivery's recorded attempts, ascending.</summary>
    /// <param name="id">The delivery id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The attempts; none for an unknown id.</returns>
    Task<IReadOnlyList<StoredDeliveryAttempt>> AttemptsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts what <paramref name="filter"/> selects, exactly, samples the first ids in event order, and counts the selected
    /// deliveries whose lane has a delivery running; changes nothing.
    /// </summary>
    /// <param name="filter">Which deliveries; <see cref="DeliveryFilter.After"/> is ignored.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The count, the sample and the busy count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="DeliveryFilter.Limit"/> is zero or less, or more than 1 000.</exception>
    Task<DeliverySelection> SelectAsync(DeliveryFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a finished delivery — succeeded, dead or cancelled — back to pending, due now, with its claim count at zero and
    /// its last error cleared, and records <paramref name="entry"/> in the same transaction; its attempt history is kept.
    /// Refused — <see langword="false"/> — while a delivery of the same subscriber and partition key is running, since the
    /// reset one would run beside it.
    /// </summary>
    /// <param name="id">The delivery id.</param>
    /// <param name="entry">The log row to write when the delivery changed.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether it changed; <see langword="false"/> for an unfinished, busy or unknown delivery, which records nothing.</returns>
    /// <exception cref="InvalidOperationException">
    /// The log refused <paramref name="entry"/> — its id, or its idempotency key, is already recorded; the reset is rolled
    /// back with it, so the delivery is unchanged.
    /// </exception>
    Task<bool> RedeliverAsync(Guid id, DeliveryOperationEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts every delivery <paramref name="filter"/> selects as replayable (<see cref="DeliveryFilter.Replayable"/>, whatever the
    /// filter says) and whose lane runs nothing back to pending, as <see cref="RedeliverAsync"/> does, and records
    /// <paramref name="entry"/> with how many it reset and the first <see cref="DeliveryFilter.Limit"/> of them in event order —
    /// in one transaction, whatever the count.
    /// </summary>
    /// <remarks>
    /// A selection taken with <see cref="SelectAsync"/> under the same filter counts what a replay resets, less its
    /// <see cref="DeliverySelection.Busy"/> deliveries. A replay naming no subscriber resets only the subscribers that had a
    /// replayable delivery when its transaction read them; one whose first arrives meanwhile is left for the next replay,
    /// since the reset is serialized against each subscriber's claims and a subscriber not yet seen cannot be.
    /// </remarks>
    /// <param name="filter">Which deliveries; <see cref="DeliveryFilter.After"/> is ignored.</param>
    /// <param name="entry">The log row; its key, when present, may be recorded once per scope.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The recorded row, or <see langword="null"/> — nothing reset — when the key is already recorded.</returns>
    /// <exception cref="InvalidOperationException">
    /// The log refused <paramref name="entry"/> for its id, not for its key; nothing is reset.
    /// </exception>
    Task<DeliveryOperationEntry?> ReplayAsync(DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Pauses or resumes a subscriber, recording <paramref name="entry"/> in the same transaction when the state changed.</summary>
    /// <remarks>
    /// A paused subscriber's deliveries are still fanned out and accumulate; a claim takes none of them until it is resumed,
    /// and its lanes then drain in order. A delivery already running when the pause commits finishes. The pause is
    /// serialized against the subscriber's claims, so once it has returned no claim takes the subscriber's deliveries.
    /// </remarks>
    /// <param name="subscriber">The subscriber id.</param>
    /// <param name="paused">Whether it should be paused.</param>
    /// <param name="entry">The log row; a pause also stores its instant, actor and reason on the control.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the state changed; <see langword="false"/> — pausing a paused subscriber, resuming a running one — records nothing.</returns>
    /// <exception cref="InvalidOperationException">
    /// The log refused <paramref name="entry"/> — its id, or its idempotency key, is already recorded; the change is rolled
    /// back with it.
    /// </exception>
    Task<bool> SetPausedAsync(string subscriber, bool paused, DeliveryOperationEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The paused subscribers, by id.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The controls.</returns>
    Task<IReadOnlyList<SubscriberControl>> ControlsAsync(CancellationToken cancellationToken = default);

    /// <summary>The log row a caller's key was recorded under.</summary>
    /// <param name="scope">The caller's identity.</param>
    /// <param name="key">The caller's key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    Task<DeliveryOperationEntry?> FindOperationAsync(string scope, string key, CancellationToken cancellationToken = default);
}
