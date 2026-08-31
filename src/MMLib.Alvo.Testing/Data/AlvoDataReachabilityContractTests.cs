using MMLib.Alvo.Data;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// Behavioural contract every <see cref="IAlvoDataReachability"/> implementation must satisfy — the three
/// obligations that port's remarks impose on an implementer, asserted generically so a third-party driver has
/// something to verify against.
/// </summary>
/// <remarks>
/// <para>
/// This exists for the reason every other port in this repository has one
/// (<see cref="Migrations.SchemaMigratorContractTests"/>,
/// <see cref="Migrations.DescriptorVersionStoreContractTests"/>, <see cref="Events.OutboxStoreContractTests"/>):
/// §0 principle 1 asks for the contract before the implementation, and prose obligations that nothing asserts
/// are obligations an implementer discovers in production. The three here are not stylistic — a health check
/// built on this port answers wrongly if any of them is broken.
/// </para>
/// <para>
/// <b>Unreachable must be an answer.</b> An implementation that threw would make "the store is away" — the
/// expected condition during a database restart — indistinguishable from a defect in the probe itself, and the
/// reason would never reach the log at the level an operator reads.
/// </para>
/// <para>
/// <b>A cancelled probe must throw.</b> A readiness check bounds the probe with a token, and the framework
/// reports the resulting cancellation as a timeout. An implementation that answered "unreachable" instead
/// would report a probe that is merely too slow as a database outage — the same wrong page, from the opposite
/// direction.
/// </para>
/// <para>
/// <b>And it must be repeatable.</b> A readiness endpoint probes on every request, so an implementation that
/// cached its first answer, or held a connection it closed, would report a store that came back as still away
/// — or one that went away as still there.
/// </para>
/// </remarks>
public abstract class AlvoDataReachabilityContractTests
{
    /// <summary>A probe over a store that really can be reached.</summary>
    protected abstract IAlvoDataReachability CreateReachable();

    /// <summary>A probe over a store that really cannot be reached at all.</summary>
    protected abstract IAlvoDataReachability CreateUnreachable();

    /// <summary>
    /// Skips when this engine cannot run here — a Testcontainers driver on a Windows-container runner.
    /// </summary>
    /// <remarks>
    /// Virtual with an empty body, so an engine that always runs (SQLite) inherits it and one that does not
    /// overrides it. Every fact below calls it first, so an unavailable engine skips instead of failing on a
    /// connection string the fixture never produced.
    /// </remarks>
    protected virtual void EnsureEngineAvailable()
    {
    }

    /// <summary>A store that answers is reported reachable, and carries no failure.</summary>
    [Fact]
    public async Task A_store_that_answers_is_reachable_and_carries_no_failure()
    {
        EnsureEngineAvailable();

        var reachability = await CreateReachable().ProbeAsync(TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeTrue();
        reachability.Failure.ShouldBeNull("a reachable store has no failure to report");
    }

    /// <summary>A store that cannot be reached is <em>answered</em>, not thrown, and carries its reason.</summary>
    [Fact]
    public async Task A_store_that_cannot_be_reached_is_answered_rather_than_thrown()
    {
        EnsureEngineAvailable();

        var reachability = await CreateUnreachable().ProbeAsync(TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeFalse();
        reachability.Failure.ShouldNotBeNull(
            "an operator needs the reason, even though the probe's own caller never publishes it");
    }

    /// <summary>
    /// A probe whose bound has already elapsed throws rather than answering — so a probe that is merely too
    /// slow is never reported as a store that is away.
    /// </summary>
    [Fact]
    public async Task A_cancelled_probe_throws_rather_than_answering_unreachable()
    {
        EnsureEngineAvailable();
        using var elapsed = new CancellationTokenSource();
        await elapsed.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await CreateReachable().ProbeAsync(elapsed.Token));
    }

    /// <summary>
    /// Probing twice answers twice. A readiness endpoint asks on every request, so a one-shot probe would
    /// report a store that came back as still away.
    /// </summary>
    [Fact]
    public async Task Probing_twice_answers_twice()
    {
        EnsureEngineAvailable();
        var reachability = CreateReachable();

        var first = await reachability.ProbeAsync(TestContext.Current.CancellationToken);
        var second = await reachability.ProbeAsync(TestContext.Current.CancellationToken);

        first.IsReachable.ShouldBeTrue();
        second.IsReachable.ShouldBeTrue("a probe that answered once must answer again");
    }
}
