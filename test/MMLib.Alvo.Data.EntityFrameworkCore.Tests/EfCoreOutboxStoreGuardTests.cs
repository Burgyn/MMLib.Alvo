using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What the outbox store refuses before it reaches storage at all. Every fact here is about a dispatcher
/// calling the port wrongly — a claim with no claimant, an empty batch, a lease of nothing, a backoff that
/// runs backwards — and about that being answered as a broken call rather than as whatever the database
/// made of it.
/// </summary>
/// <remarks>
/// The connection factory refuses to hand out a connection, which is what makes these facts discriminating
/// rather than merely green: a guard that stopped refusing does not quietly claim an empty batch from a test
/// database, it walks into the factory and comes back with the wrong exception entirely.
/// </remarks>
public class EfCoreOutboxStoreGuardTests
{
    /// <summary>
    /// None of the three collaborators is optional. Two of them would otherwise be stored as
    /// <see langword="null"/> and fail on some later call, by which point the host has no way to tell which
    /// registration was wrong.
    /// </summary>
    [Fact]
    public void The_store_requires_every_collaborator()
    {
        Should.Throw<ArgumentNullException>(() => new EfCoreOutboxStore(null!, new AlvoOptions(), TimeProvider.System))
            .ParamName.ShouldBe("connections");
        Should.Throw<ArgumentNullException>(() => new EfCoreOutboxStore(Connections(), null!, TimeProvider.System))
            .ParamName.ShouldBe("options");
        Should.Throw<ArgumentNullException>(() => new EfCoreOutboxStore(Connections(), new AlvoOptions(), null!))
            .ParamName.ShouldBe("time");
    }

    /// <summary>
    /// A claimant is what distinguishes a held row from a released one in the table's state machine, so an
    /// unnamed one would stamp a claim nothing could later be recognised as holding.
    /// </summary>
    [Fact]
    public async Task A_claim_with_no_claimant_is_refused()
        => await Should.ThrowAsync<ArgumentException>(
            async () => await Store().ClaimAsync(" ", BatchSize, MaxAttempts, Lease));

    /// <summary>
    /// A batch of nothing and an attempt ceiling of nothing are both a caller asking for a claim that can
    /// never return a row, which is a mistake rather than an idle poll.
    /// </summary>
    /// <param name="batchSize">How many entries to claim.</param>
    /// <param name="maxAttempts">The attempt ceiling past which an entry stops being claimed.</param>
    [Theory]
    [InlineData(0, MaxAttempts)]
    [InlineData(-1, MaxAttempts)]
    [InlineData(BatchSize, 0)]
    [InlineData(BatchSize, -1)]
    public async Task A_claim_outside_its_bounds_is_refused(int batchSize, int maxAttempts)
        => await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await Store().ClaimAsync(Claimant, batchSize, maxAttempts, Lease));

    /// <summary>
    /// A lease of zero would make every entry the claim just stamped immediately stale to the next tick —
    /// the duplicate delivery the held branch of the claimability predicate exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_claim_with_no_lease_is_refused()
        => await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await Store().ClaimAsync(Claimant, BatchSize, MaxAttempts, TimeSpan.Zero));

    /// <summary>There is no envelope to append, and no row worth writing without one.</summary>
    [Fact]
    public async Task An_append_requires_the_event()
        => await Should.ThrowAsync<ArgumentNullException>(async () => await Store().AppendAsync(null!));

    /// <summary>
    /// A negative backoff stamps <c>claimed_at</c> in the past, which is the released state read as
    /// "claimable already" — so it is refused rather than silently turned into the immediate retry
    /// <see cref="TimeSpan.Zero"/> already expresses.
    /// </summary>
    [Fact]
    public async Task A_release_with_a_backoff_that_runs_backwards_is_refused()
        => await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await Store().ReleaseAsync(Guid.NewGuid(), TimeSpan.FromSeconds(-1)));

    private static EfCoreOutboxStore Store() => new(Connections(), new AlvoOptions(), TimeProvider.System);

    private static RelationalConnectionFactory Connections() =>
        new(() => throw new InvalidOperationException("no guarded call reaches a connection"));

    private static TimeSpan Lease => TimeSpan.FromMinutes(5);

    private const string Claimant = "dispatcher";
    private const int BatchSize = 10;
    private const int MaxAttempts = 5;
}
