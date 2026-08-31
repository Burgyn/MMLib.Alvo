namespace MMLib.Alvo.Data;

/// <summary>
/// A cheap "can this process still reach its store" probe — the port a readiness check asks, so the core
/// never opens a connection of its own (§0 principle 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="IAlvoData"/>, and deliberately so.</b> That port is the record contract, and
/// every implementation of it — <c>MMLib.Alvo.Testing.Data.InMemoryAlvoData</c> included — would have to grow
/// a member it has no store behind. This one is implemented by whatever ships a store: the shared EF data
/// path registers one for every relational driver, and a driver with nothing cheap to ask simply does not
/// register it.
/// </para>
/// <para>
/// <b>Not registering it is the supported way to opt out</b>, and it is the reason
/// <see cref="AlvoReachability"/> has two states rather than three. A "cannot answer" state would have to be
/// mapped to a health status: healthy is fail-open and unhealthy is a pod that never receives traffic, so
/// every mapping is wrong for somebody and the state exists only to be mis-handled. A container with no probe
/// registered reports exactly the readiness it reported before this port existed.
/// </para>
/// <para>
/// <b>What it must not do.</b> It must not read or write a record, must not apply or inspect the schema —
/// "the descriptor applied and the policy catalog is primed" is a different question with its own contributor
/// — and must not take longer than a probe can wait. The caller bounds it with the token; an implementation
/// that ignores the token is one a readiness endpoint cannot use.
/// </para>
/// </remarks>
public interface IAlvoDataReachability
{
    /// <summary>Asks the store whether it can still be reached.</summary>
    /// <remarks>
    /// <para>
    /// <b>Unreachable is a return value, not an exception.</b> A store being away is the expected condition
    /// this port exists to report — during a rolling restart of the database it is the <em>normal</em> answer
    /// — and making the normal answer exceptional would push every caller into a <c>catch</c> that cannot
    /// distinguish it from a defect in the probe itself.
    /// </para>
    /// <para>
    /// <b>A cancelled probe still throws.</b> <see cref="OperationCanceledException"/> means the caller's
    /// bound elapsed, which is a different diagnosis from "the store answered that it is away", and an
    /// implementation that reported it as unreachable would hide a probe that is simply too slow.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">The caller's bound on how long the probe may take.</param>
    /// <returns>Whether the store can be reached, and — when it cannot — why.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    ValueTask<AlvoReachability> ProbeAsync(CancellationToken cancellationToken = default);
}
