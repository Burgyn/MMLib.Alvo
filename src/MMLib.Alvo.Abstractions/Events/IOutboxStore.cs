namespace MMLib.Alvo.Events;

/// <summary>
/// One claimed outbox row, as much of it as a dispatcher needs to deliver it and decide what to do next.
/// </summary>
/// <param name="Id">The event's id, and the order the queue is claimed in.</param>
/// <param name="Type">The event type, so a dispatcher can route without deserializing the payload.</param>
/// <param name="PartitionKey">The key events for one row share; nothing reads it in this build (see the remarks).</param>
/// <param name="Payload">The stored envelope, exactly as <see cref="AlvoEventJson.Write"/> produced it.</param>
/// <param name="Attempts">How many times this entry has been claimed, <b>including</b> this claim.</param>
/// <remarks>
/// <para>
/// The payload stays JSON text rather than an <see cref="AlvoEvent"/>: a store's job is to hand back what
/// it was given, and a store that deserialized would own a second copy of the envelope's reading rules.
/// <see cref="AlvoEventJson.Read(string)"/> is the one reader, and the dispatcher calls it.
/// </para>
/// <para>
/// <see cref="PartitionKey"/> is carried with no reader in this build for the reason the column is written
/// with none: F7's per-key claim is then an additive change rather than a migration of a shipped table and
/// a widened port.
/// </para>
/// <para>
/// <see cref="Attempts"/> counts the claim that produced this entry, so a first delivery reads
/// <c>1</c> rather than <c>0</c>. That is what makes it comparable against the ceiling a caller passed to
/// <see cref="IOutboxStore.ClaimAsync"/> without the caller re-deriving it.
/// </para>
/// </remarks>
public sealed record OutboxEntry(Guid Id, string Type, string PartitionKey, string Payload, int Attempts);

/// <summary>
/// The durable queue Alvo's write path appends to and its dispatcher drains: claim a batch under a lease,
/// mark what was delivered, release what was not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this port exists at all.</b> <c>docs/architecture/package-boundary.md</c> predicted the moment:
/// <em>"A port is earned the moment a driver's system schema grows a table no store call touches — PR5's
/// outbox is the first candidate."</em> The outbox is that table, and the dispatcher that drains it lives in
/// the core, which depends on <c>MMLib.Alvo.Abstractions</c> alone — the driver's own outbox statements are
/// <see langword="internal"/> to it. Nothing else in the framework could reach them, so the debt is paid
/// here rather than by making the core depend on a driver.
/// </para>
/// <para>
/// <b>The claim protocol, in the order it happens.</b> <see cref="ClaimAsync"/> takes the oldest undelivered
/// entries, stamps them with the caller's name and the current instant, and counts the attempt.
/// <see cref="MarkDispatchedAsync"/> retires an entry for good. <see cref="ReleaseAsync"/> hands one back for
/// a later retry, no sooner than the backoff it is given. Nothing else recovers a claim a process took and then
/// died holding: that is the lease, and it is why <see cref="ClaimAsync"/> takes one rather than reading a
/// configured value.
/// </para>
/// <para>
/// <b>An entry is therefore in one of two waiting states, and an implementation must tell them apart.</b> A
/// <em>held</em> entry — claimed and not yet answered for — becomes claimable again only once its
/// <em>lease</em> expires, which is the crash-recovery path. A <em>released</em> entry becomes claimable once
/// its own backoff has passed, and its lease is irrelevant because nobody is holding it. Collapsing the two
/// into "claimable immediately on release" is what lets one restarting receiver spend an event's whole attempt
/// ceiling in milliseconds; collapsing them the other way makes every failed delivery wait out a lease sized
/// for crash recovery.
/// </para>
/// <para>
/// <b>The claim filters undelivered entries, never a high-water mark.</b> Ids are minted per process
/// (<see cref="AlvoEventId"/>) and a relational sequence commits out of order, so "delivered up to N" drops
/// an entry silently — an entry whose id sorts below one already delivered is still claimable, and an
/// implementation is held to that. This is why <see cref="OutboxEntry.Id"/> is a UUIDv7 rather than an
/// integer: the wrong implementation is unavailable rather than merely discouraged.
/// </para>
/// <para>
/// <b>Each member is one statement, and never a read followed by a write in one transaction.</b> Measured on
/// SQLite (<c>docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt</c>, Q5): that one shape
/// fails unretryably with <c>SQLITE_BUSY_SNAPSHOT</c> after burning the whole 30-second retry loop under
/// WAL, and under the shipped journal mode it fails the <em>request path</em> instead. Every other shape
/// measured waited and then succeeded. So an implementation claims with a single write, or with the first
/// statement of a write-first transaction — a "read the batch, then update it" implementation is the one way
/// to make a dispatcher take the request path down with it.
/// </para>
/// <para>
/// <b>Ordering.</b> Per-entity-key ordering holds with one dispatcher <em>and</em> no two events for one key
/// inside the same millisecond. Nothing in this port can widen that, and a second claimant does not: an
/// implementation must let the loser claim <em>nothing</em> rather than the entries the winner just took.
/// </para>
/// </remarks>
public interface IOutboxStore
{
    /// <summary>
    /// Brings the queue's own storage up, so every other member can be called against a fresh database.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// Idempotent and safe from several processes at once, for the reason
    /// <see cref="Migrations.IAppliedSchemaStore"/> carries the same requirement: a host may perform it
    /// twice, and replicas cold-starting against one empty database perform it at the same instant.
    /// </remarks>
    Task EnsureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends one <b>custom application event</b> to the queue, so the dispatcher claims it like any other.
    /// </summary>
    /// <param name="envelope">The event to append; its <see cref="AlvoEvent.Id"/> is the entry's own id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// <para>
    /// <b>A data event never travels through here, and that asymmetry is the design rather than an
    /// oversight.</b> A create, update or delete appends its event on <em>the caller's own transaction and
    /// connection</em>, which is what makes "no lost and no phantom event" true at all; a driver does that
    /// itself, with no port in the way. A custom application event has no data change to be atomic with, so it
    /// is appended by one autocommit statement like every other member here — and a host that needs its own
    /// write and its own event to commit together does not get that from this member.
    /// </para>
    /// <para>
    /// <b>One statement, on the port's standing rule.</b> Never a read followed by a write in one
    /// transaction: that is the single shape measured to fail unretryably on SQLite (spike Q5), and an append
    /// that first checked whether the id was taken would be exactly it. A duplicate id is the caller's
    /// mistake and surfaces as the primary key violation it is.
    /// </para>
    /// </remarks>
    Task AppendAsync(AlvoEvent envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> undelivered entries for <paramref name="claimant"/>, oldest
    /// first, and returns them in the order they must be delivered in.
    /// </summary>
    /// <param name="claimant">Who is claiming, recorded on each entry so an abandoned claim is attributable.</param>
    /// <param name="batchSize">The most entries to claim in this call.</param>
    /// <param name="maxAttempts">The attempt ceiling: an entry that has already been claimed this many times is left alone.</param>
    /// <param name="lease">How long this claim holds before another claimant may take the entry back.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The claimed entries, ascending by <see cref="OutboxEntry.Id"/>; empty when nothing is claimable.</returns>
    /// <remarks>
    /// <para>
    /// <b>The ceiling is this build's stand-in for a dead-letter queue.</b> Past it an entry stops being
    /// claimed, so one poison event cannot occupy the pump forever; nothing deletes or moves it, so it is
    /// still there to be inspected.
    /// </para>
    /// <para>
    /// <b>The returned order is the store's promise, not the engine's.</b> <c>RETURNING</c> row order is
    /// arbitrary in measured fact on both shipped engines (spike Q3, <c>RETURNING already sorted: False</c>),
    /// so an implementation that orders only inside the database returns a correctly <em>chosen</em> batch in
    /// an arbitrary order.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<OutboxEntry>> ClaimAsync(
        string claimant,
        int batchSize,
        int maxAttempts,
        TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>Retires <paramref name="id"/>: it was delivered and must never be claimed again.</summary>
    /// <param name="id">The entry's id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// Retiring outlives the lease: an entry marked delivered stays retired once its lease expires, which is
    /// the difference between "this claim is over" and "this entry is done".
    /// </remarks>
    Task MarkDispatchedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands <paramref name="id"/> back, claimable again once <paramref name="retryAfter"/> has passed.
    /// </summary>
    /// <param name="id">The entry's id.</param>
    /// <param name="retryAfter">
    /// How long the entry stays unclaimable. <see cref="TimeSpan.Zero"/> means "claimable immediately", which is
    /// what a caller handing an entry straight back with no failed delivery behind it asks for.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// <para>
    /// The attempt count is <b>not</b> rolled back. Releasing is how a dispatcher says "I could not deliver
    /// this", and an implementation that reset the count would make the ceiling in
    /// <see cref="ClaimAsync"/> unreachable — the poison entry would be retried forever.
    /// </para>
    /// <para>
    /// <b><paramref name="retryAfter"/> is what makes the ceiling a bound on time and not only on count.</b>
    /// Without it a released entry is claimable on the caller's very next claim, so a receiver that is
    /// restarting exhausts <c>maxAttempts</c> in milliseconds and the event is abandoned permanently — and this
    /// build has no dead-letter queue to recover it from. An implementation that ignores the parameter is
    /// therefore not merely imprecise: it removes the only thing that lets a delivery survive a redeploy.
    /// </para>
    /// <para>
    /// It is measured from the implementation's <em>own</em> clock rather than passed as an instant, for the
    /// reason <see cref="ClaimAsync"/> takes a lease rather than reading one: the store is what stamps the row,
    /// so the store is what must read the clock the stamp is compared against.
    /// </para>
    /// </remarks>
    Task ReleaseAsync(Guid id, TimeSpan retryAfter, CancellationToken cancellationToken = default);
}
