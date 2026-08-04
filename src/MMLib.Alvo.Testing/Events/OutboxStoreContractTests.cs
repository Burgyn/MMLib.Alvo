using MMLib.Alvo.Events;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Events;

/// <summary>
/// The claim protocol every <see cref="IOutboxStore"/> must implement identically, on every engine.
/// </summary>
/// <remarks>
/// Inherited by both shipped drivers unchanged, and by any external one: the queue's state machine —
/// unclaimed, claimed under a lease, delivered, past the ceiling — is engine-agnostic by construction, and
/// two per-engine copies of these facts would be two chances for the engines to stop being asked the same
/// question. Everything engine-specific arrives through <see cref="WorldAsync"/>.
/// </remarks>
public abstract class OutboxStoreContractTests
{
    /// <summary>Builds a store over fresh, already-created storage with an empty queue.</summary>
    protected abstract Task<IOutboxStoreWorld> WorldAsync();

    /// <summary>Skips the test when the engine is unavailable on this runner. A no-op for an in-process engine.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    /// <summary>A claim takes the oldest entries, and hands them back oldest first.</summary>
    [Fact]
    public async Task A_claim_returns_the_oldest_undispatched_entries_in_order()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 10);

        var claimed = await world.Store.ClaimAsync(Claimant, batchSize: 4, MaxAttempts, _lease, Ct);

        claimed.Select(entry => entry.Id).ShouldBe(ids.Take(4));
    }

    /// <summary>
    /// <c>RETURNING</c>'s row order is documented as arbitrary on both engines, so the store re-sorts in
    /// process. Without that, "in order" above would hold only by luck.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured, not merely documented: spike Q3 reports <c>RETURNING already sorted: False</c> for SQLite
    /// <em>and</em> PostgreSQL.
    /// </para>
    /// <para>
    /// <b>What makes this fact bite is <see cref="IOutboxStoreWorld.SeedAsync"/> appending out of id order,
    /// not the batch size.</b> Measured while proving it: with the entries appended <em>ascending</em>, an
    /// engine's physical row order equals the queue order and deleting the shipped store's re-sort left this
    /// fact green on both engines at <see cref="UnsortedReturningBatchSize"/> entries; with them appended in
    /// reverse it goes red on both. That is why the obligation is written on the world's own member rather
    /// than left as a batch size to tune.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_claim_is_sorted_in_process_because_returning_order_is_arbitrary()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.SeedAsync(UnsortedReturningBatchSize);

        var claimed = await world.Store.ClaimAsync(
            Claimant, UnsortedReturningBatchSize, MaxAttempts, _lease, Ct);

        claimed.Select(entry => entry.Id.ToString())
            .ShouldBe(claimed.Select(entry => entry.Id.ToString()).Order(StringComparer.Ordinal));
    }

    /// <summary>A live claim is exclusive: claiming again while the lease holds takes nothing.</summary>
    [Fact]
    public async Task A_claimed_entry_is_not_claimed_twice_while_its_lease_holds()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.SeedAsync(count: 4);

        var first = await world.Store.ClaimAsync(Claimant, batchSize: 4, MaxAttempts, _lease, Ct);
        var second = await world.Store.ClaimAsync(Claimant, batchSize: 4, MaxAttempts, _lease, Ct);

        first.Count.ShouldBe(4);
        second.ShouldBeEmpty();
    }

    /// <summary>
    /// The recovery path the crash criterion rests on: a claim whose process died is re-claimed once its
    /// lease expires. Without it, one kill strands an event forever.
    /// </summary>
    [Fact]
    public async Task A_claim_whose_lease_expired_is_claimed_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync("dead-worker", batchSize: 1, MaxAttempts, _lease, Ct);

        world.Advance(_lease + TimeSpan.FromSeconds(1));
        var reclaimed = await world.Store.ClaimAsync("worker-2", batchSize: 1, MaxAttempts, _lease, Ct);

        reclaimed.ShouldHaveSingleItem().Attempts.ShouldBe(2);
    }

    /// <summary>Delivery is final: an expired lease does not resurrect a delivered entry.</summary>
    [Fact]
    public async Task A_dispatched_entry_is_never_claimed_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct);

        await world.Store.MarkDispatchedAsync(ids[0], Ct);
        world.Advance(_lease + TimeSpan.FromSeconds(1));

        (await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct)).ShouldBeEmpty();
    }

    /// <summary>
    /// A release with a backoff really holds the entry for that long, and the backoff is measured against the
    /// <b>current instant</b> rather than against the lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves are what make this fact bite in both directions. The claim <em>before</em> the clock moves
    /// must come back empty, or the backoff does nothing and one restarting receiver spends an event's whole
    /// attempt ceiling in milliseconds. The claim after it must succeed on a clock that has moved by the backoff
    /// and by <b>far less than the lease</b>, or a released entry is waiting out a crash-recovery window it does
    /// not need — which would make every failed delivery five minutes late at the shipped defaults.
    /// </para>
    /// <para>
    /// The attempt count is asserted too, because the release must not roll it back: that is what keeps the
    /// ceiling reachable at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_released_entry_is_held_for_its_backoff_and_not_for_the_lease()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct);

        await world.Store.ReleaseAsync(ids[0], _backoff, Ct);
        var tooSoon = await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct);

        world.Advance(_backoff + TimeSpan.FromSeconds(1));
        var reclaimed = await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct);

        tooSoon.ShouldBeEmpty($"a backoff of {_backoff} must hold the entry for that long");
        reclaimed.ShouldHaveSingleItem().Attempts.ShouldBe(2);
        _backoff.ShouldBeLessThan(_lease, "the fact would not distinguish the backoff from the lease otherwise");
    }

    /// <summary>
    /// A release with <see cref="TimeSpan.Zero"/> is claimable at once — the shape a caller handing an entry
    /// straight back with no failed delivery behind it asks for, and the one the port promises.
    /// </summary>
    [Fact]
    public async Task A_release_with_no_backoff_is_claimable_at_once()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct);

        await world.Store.ReleaseAsync(ids[0], TimeSpan.Zero, Ct);

        (await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct))
            .ShouldHaveSingleItem();
    }

    /// <summary>
    /// This build's stand-in for a DLQ is an attempt ceiling plus a loud log: past the ceiling the entry
    /// stops being claimed, so one poison event cannot occupy the pump forever.
    /// </summary>
    /// <remarks>
    /// The clock moves past each attempt's backoff, because that is the only way the ceiling is reachable at all
    /// now — which is the point: reaching it takes <em>time</em> and not merely a loop.
    /// </remarks>
    [Fact]
    public async Task An_entry_past_the_attempt_ceiling_is_no_longer_claimed()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 1);

        foreach (var _ in Enumerable.Range(0, MaxAttempts))
        {
            await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct);
            await world.Store.ReleaseAsync(ids[0], TimeSpan.Zero, Ct);
        }

        (await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct)).ShouldBeEmpty();
    }

    /// <summary>
    /// A relational sequence commits out of order, so a "delivered up to N" watermark drops a row silently.
    /// This proves the claim is a flag filter and not a watermark: an entry whose id sorts <b>below</b> one
    /// already delivered is still claimed.
    /// </summary>
    [Fact]
    public async Task An_entry_whose_id_sorts_below_an_already_dispatched_one_is_still_claimed()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var late = await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct);
        await world.Store.MarkDispatchedAsync(late[0], Ct);

        var earlier = await world.SeedWithExplicitIdAsync(Guid.Empty);

        (await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct))
            .ShouldHaveSingleItem().Id.ShouldBe(earlier);
    }

    private const int MaxAttempts = 5;
    private const int UnsortedReturningBatchSize = 50;
    private const string Claimant = "worker-1";

    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A retry backoff far shorter than <see cref="_lease"/>, so the backoff fact can tell the two apart.
    /// </summary>
    private static readonly TimeSpan _backoff = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
