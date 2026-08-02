using Microsoft.Extensions.Diagnostics.HealthChecks;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The one contributor to <see cref="AlvoHealth.ReadinessPath"/> that Alvo ships: has the boot decided the
/// schema and primed the policy catalog this process would serve from.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="HealthStatus.Unhealthy"/>, never <see cref="HealthStatus.Degraded"/> — a correctness
/// requirement, not a shade of meaning.</b> ASP.NET Core's default <c>ResultStatusCodes</c> map
/// <see cref="HealthStatus.Degraded"/> to <b>200</b>, and Kubernetes counts any 2xx as a passing probe, so a
/// degraded schema gate is invisible to an <c>httpGet</c> probe and the pod receives traffic with no schema
/// behind it. The default status map is deliberately left alone — a host's own checks may mean Degraded
/// honestly — so the guarantee has to live here, in what this check reports.
/// </para>
/// <para>
/// <b>The description reports the phase and never <see cref="AlvoBootState.Failure"/>.</b> For a stage-1 or
/// stage-2 refusal that reason is the database provider's own message, which can carry a connection string or a
/// filesystem path — and a health check's description travels further than it looks:
/// <c>DefaultHealthCheckService</c> logs it, every <c>IHealthCheckPublisher</c> receives it, and a host that
/// maps a readiness endpoint of its own with a verbose response writer publishes it to an anonymous caller.
/// The operator gets the whole reason on stderr and in the log; a probe gets a phase (design deviation 59).
/// </para>
/// <para>
/// An unprimed catalog denies every operation, so reporting anything but Ready for a phase that is not Ready is
/// the fail-closed direction rather than a courtesy: <see cref="AlvoBootPhase.Pending"/> is
/// <c>default(AlvoBootPhase)</c> precisely so a probe answering before any boot published anything says "not
/// ready".
/// </para>
/// </remarks>
/// <param name="state">What Alvo's boot published about itself.</param>
internal sealed class AlvoSchemaHealthCheck(AlvoBootState state) : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var phase = state.Phase;

        return Task.FromResult(phase is AlvoBootPhase.Ready
            ? HealthCheckResult.Healthy(Describe(phase))
            : HealthCheckResult.Unhealthy(Describe(phase)));
    }

    private static string Describe(AlvoBootPhase phase) => $"Alvo's boot is {phase}.";
}
