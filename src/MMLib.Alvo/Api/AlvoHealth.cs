namespace MMLib.Alvo.Api;

/// <summary>
/// Where Alvo's two probes answer, and the tag that puts a health check on the readiness one.
/// </summary>
/// <remarks>
/// Constants rather than literals because the same two routes are written into a container's
/// <c>healthcheck</c>, into a Kubernetes <c>httpGet</c> probe, and into every fact that measures them — and a
/// probe path that drifts in one of those three places is invisible until a deployment reports healthy while
/// serving nothing.
/// </remarks>
public static class AlvoHealth
{
    /// <summary>The route a liveness probe calls: <em>is this process alive</em>.</summary>
    /// <remarks>
    /// It evaluates <b>no</b> health check at all, so nothing anyone registers can make it fail. A failing
    /// liveness probe has the container killed and restarted, which is the wrong answer to "the migration job
    /// has not run yet" — the Kubernetes documentation warns that exactly this mistake causes cascading
    /// failures under load. Everything conditional belongs on <see cref="ReadinessPath"/>.
    /// </remarks>
    public const string LivenessPath = "/health/live";

    /// <summary>The route a readiness probe calls: <em>may this process receive traffic</em>.</summary>
    /// <remarks>
    /// 503 until Alvo's boot has primed the schema and the policy catalog, 200 once it has — and 503 again if
    /// the store stops answering, which is the <em>continuing</em> half a boot that ran once cannot report. A
    /// failing readiness probe only removes the pod's address from the service's endpoints, which is precisely
    /// the right consequence. The response body carries the boot phase and nothing else: the reason a boot refused can
    /// hold a connection string, and this route is unauthenticated by design.
    /// </remarks>
    public const string ReadinessPath = "/health/ready";

    /// <summary>The tag a health check carries to be evaluated by <see cref="ReadinessPath"/>.</summary>
    /// <remarks>
    /// Readiness selects by tag while liveness selects nothing, and the asymmetry is deliberate: a check
    /// registered without much thought lands in readiness, where being wrong costs the pod its traffic rather
    /// than its process.
    /// </remarks>
    public const string ReadyTag = "ready";

    /// <summary>The name Alvo's own schema-applied check is registered under.</summary>
    internal const string SchemaCheckName = "alvo-schema";

    /// <summary>The name Alvo's own database-reachability check is registered under.</summary>
    internal const string DatabaseCheckName = "alvo-database";

    /// <summary>
    /// How long <see cref="DatabaseCheckName"/> may take before the health-check service reports it as failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Carried by <c>HealthCheckRegistration.Timeout</c> rather than by the check itself</b>, so the
    /// framework's own linked cancellation source is what enforces it. It is a <em>cooperative</em> bound, and
    /// that is worth stating precisely: the framework cancels the token it handed the check and then awaits it,
    /// so a probe that <b>honours</b> its token turns into a 503 while one that ignores the token holds the
    /// request. Honouring it is the obligation
    /// <see cref="MMLib.Alvo.Data.IAlvoDataReachability.ProbeAsync"/> states and the reachability contract suite
    /// asserts; for a probe that breaks it, the backstop is the orchestrator's own probe timeout, which is
    /// outside this process either way.
    /// </para>
    /// <para>
    /// <b>Two seconds, and a constant rather than configuration.</b> A refused connection fails in
    /// milliseconds; the case a bound exists for is a <em>hanging</em> one — packet loss to a database whose
    /// driver would otherwise wait out its own connect timeout, fifteen seconds on Npgsql — and a readiness
    /// answer that arrives after the orchestrator's own probe timeout is a failure with extra steps. The value
    /// that would actually need tuning is that orchestrator's timeout, which lives outside this process, so a
    /// knob here would configure the wrong end.
    /// </para>
    /// </remarks>
    internal static TimeSpan DatabaseProbeTimeout { get; } = TimeSpan.FromSeconds(2);
}
