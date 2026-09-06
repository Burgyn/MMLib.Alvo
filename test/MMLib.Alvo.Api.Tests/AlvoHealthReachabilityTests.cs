using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Data;
using MMLib.Alvo.Migrations;
using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// #133 over the readiness endpoint: a database that has gone away after boot drains the pod, and the reason
/// stays off the wire.
/// </summary>
/// <remarks>
/// <b>Every fact here substitutes the port rather than breaking a real database</b>, and that is the point of
/// the port existing: "the store is away" is a state no in-process fixture can produce on demand without one.
/// The real-engine legs — a database that genuinely answers, and one that genuinely cannot be opened — are
/// <c>SqliteReachabilityTests</c>' and the PostgreSQL integration suite's.
/// </remarks>
public class AlvoHealthReachabilityTests
{
    /// <summary>A store that cannot be reached is 503, even though the boot is Ready.</summary>
    /// <remarks>
    /// The boot phase in the body is asserted too, and it is the discriminating half: it is
    /// <see cref="AlvoBootPhase.Ready"/>, so the 503 can only have come from the reachability contributor. A
    /// fact that asserted the status alone would pass just as well over a host whose boot never ran.
    /// </remarks>
    [Fact]
    public async Task Readiness_is_503_when_the_database_cannot_be_reached()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(Away)));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        readiness.Body.ShouldBe(nameof(AlvoBootPhase.Ready), "the boot is ready; only reachability is not");
    }

    /// <summary>The control: a store that answers leaves readiness at 200.</summary>
    [Fact]
    public async Task Readiness_is_200_when_the_database_can_be_reached()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(Answering)));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A store that cannot be reached does not take <b>liveness</b> down. That is the whole reason readiness
    /// is the route this contributes to: a database outage must drain the pod's traffic, never restart-loop
    /// the container.
    /// </summary>
    [Fact]
    public async Task An_unreachable_database_does_not_take_liveness_down()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(Away)));

        var liveness = await world.ProbeAsync(AlvoHealth.LivenessPath);

        liveness.Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A probe that never returns is answered <b>503</b> rather than held — the registration's own timeout,
    /// which is why the check carries no timeout of its own.
    /// </summary>
    /// <remarks>
    /// This is the fact that pins <c>HealthCheckRegistration.Timeout</c> being honoured, rather than the
    /// documentation being taken on trust. Dropping the timeout from the registration turns it from a 503
    /// into a request that hangs until the fixture's own cancellation, which is a failing fact rather than a
    /// slow one.
    /// </remarks>
    [Fact]
    public async Task A_probe_that_hangs_is_a_503_and_not_a_held_request()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(HangingAsync)));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// With <b>no</b> probe registered, readiness is exactly what it was before this port existed — the
    /// supported opt-out for a driver with nothing cheap to ask.
    /// </summary>
    /// <remarks>
    /// Fail-open, deliberately: readiness is an availability gate rather than an authorization one, and a
    /// third-party driver that ships without a probe must not make every pod permanently unready. Both
    /// in-repo drivers register one, so this state is reachable only on purpose.
    /// </remarks>
    [Fact]
    public async Task With_no_probe_registered_readiness_is_unchanged()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: services => services.RemoveAll<IAlvoDataReachability>()));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The readiness body reports the boot phase and <b>never</b> the reason the store gave — which really
    /// does carry a credential here, so the guard is not vacuous.
    /// </summary>
    [Fact]
    public async Task A_readiness_body_never_carries_the_reason_the_store_gave()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(_ => Unreachable(SecretInTheReason))));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        readiness.Body.ShouldNotContain("hunter2", Case.Sensitive);
        readiness.Body.ShouldBe(nameof(AlvoBootPhase.Ready));
    }

    private const string SecretInTheReason = "Host=db.internal;Username=alvo;Password=hunter2";

    private static Action<IServiceCollection> Probe(
        Func<CancellationToken, ValueTask<AlvoReachability>> answer) =>
        services => services.AddSingleton<IAlvoDataReachability>(new StubReachability(answer));

    private static ValueTask<AlvoReachability> Answering(CancellationToken cancellationToken) =>
        ValueTask.FromResult(AlvoReachability.Reachable);

    private static ValueTask<AlvoReachability> Away(CancellationToken cancellationToken) =>
        Unreachable("the store is away");

    private static ValueTask<AlvoReachability> Unreachable(string reason) =>
        ValueTask.FromResult(AlvoReachability.Unreachable(new InvalidOperationException(reason)));

    /// <summary>
    /// A probe that answers only when its token is cancelled, so the registration's timeout is the only thing
    /// that can end it.
    /// </summary>
    /// <remarks>
    /// It honours the token rather than ignoring it outright, because an implementation that never observed
    /// cancellation would leave a task running for the rest of the test run — and the claim under test is
    /// about the framework's bound, not about a leaked task.
    /// </remarks>
    /// <param name="cancellationToken">The bound the health-check registration imposed.</param>
    private static async ValueTask<AlvoReachability> HangingAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

        return AlvoReachability.Reachable;
    }

    private sealed class StubReachability(Func<CancellationToken, ValueTask<AlvoReachability>> answer)
        : IAlvoDataReachability
    {
        public ValueTask<AlvoReachability> ProbeAsync(CancellationToken cancellationToken = default) =>
            answer(cancellationToken);
    }
}
