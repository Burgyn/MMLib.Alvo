using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The second contributor to <see cref="AlvoHealth.ReadinessPath"/>: can this process still reach the store
/// it serves from (#133).
/// </summary>
/// <remarks>
/// <para>
/// <b>It answers the <em>continuing</em> question, which is the one nothing else asked.</b>
/// <see cref="AlvoSchemaHealthCheck"/> reports what the boot decided, and the boot ran once, before the
/// server bound — so a database that goes away afterwards was invisible to both probes and an orchestrator
/// had nothing to drain traffic on.
/// </para>
/// <para>
/// <b><see cref="HealthStatus.Unhealthy"/>, never <see cref="HealthStatus.Degraded"/></b>, for the reason
/// <see cref="AlvoSchemaHealthCheck"/> gives: the framework maps <see cref="HealthStatus.Degraded"/> to
/// <b>200</b> and Kubernetes counts any 2xx as a passing probe, so a degraded gate is no gate at all.
/// </para>
/// <para>
/// <b>The description is constant and the reason goes to the log.</b> A check's description reaches
/// <c>DefaultHealthCheckService</c>'s log, every <see cref="IHealthCheckPublisher"/>, and any verbose
/// response writer a host maps of its own — while the driver's message for an unreachable store can carry a
/// connection string. The failure is not passed to <see cref="HealthCheckResult"/> either, for the same
/// reason and not as an economy. The operator reads it in the log; the probe reads a status code (design
/// deviation 59).
/// </para>
/// <para>
/// <b>No probe registered is <see cref="HealthStatus.Healthy"/>, and the description is honest about it.</b>
/// Not registering <see cref="IAlvoDataReachability"/> is the supported opt-out for a driver with nothing
/// cheap to ask, so a container without one reports exactly the readiness it reported before this check
/// existed. The description says which of the two happened, because "healthy" for a question nobody asked is
/// the one answer here that could mislead a reader of the log.
/// </para>
/// <para>
/// <b>Nothing is caught.</b> An unreachable store is a return value, not an exception
/// (<see cref="IAlvoDataReachability.ProbeAsync"/> says so), and anything a probe does throw is either the
/// registration's timeout or a defect — both of which the health-check service reports as this registration's
/// failure status, with its own log record. A <c>catch</c> here would flatten the two into one diagnosis.
/// </para>
/// </remarks>
/// <param name="reachability">The store's own probe, or <see langword="null"/> when no driver registered one.</param>
/// <param name="logger">Where an unreachable store's reason is written for the operator who has to read it.</param>
internal sealed partial class AlvoReachabilityHealthCheck(
    IAlvoDataReachability? reachability, ILogger<AlvoReachabilityHealthCheck> logger) : IHealthCheck
{
    private const string NoProbeIsRegistered = "No Alvo data-reachability probe is registered.";
    private const string TheStoreAnswered = "Alvo can reach its store.";
    private const string TheStoreIsAway = "Alvo cannot reach its store.";

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (reachability is null)
        {
            return HealthCheckResult.Healthy(NoProbeIsRegistered);
        }

        var probed = await reachability.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (probed.IsReachable)
        {
            return HealthCheckResult.Healthy(TheStoreAnswered);
        }

        TheStoreCannotBeReached(logger, probed.Failure);

        return HealthCheckResult.Unhealthy(TheStoreIsAway);
    }

    /// <summary>The one log record, carrying the reason the probe's answer withholds from the wire.</summary>
    /// <remarks>
    /// Source-generated because <c>CA1848</c> is an error in this repository. The reason is passed as the
    /// record's exception rather than formatted into the message, so its type and inner exceptions survive
    /// into whatever the host's logging does with it.
    /// </remarks>
    /// <param name="logger">The logger this check writes through.</param>
    /// <param name="failure">Why the store could not be reached.</param>
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo cannot reach its store, so readiness reports Unhealthy and this pod should be drained.")]
    private static partial void TheStoreCannotBeReached(ILogger logger, Exception? failure);
}
