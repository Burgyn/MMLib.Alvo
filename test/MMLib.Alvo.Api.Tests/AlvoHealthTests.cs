using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The two probes <c>MapAlvoHealth</c> maps: liveness says the process is up, readiness says Alvo's boot
/// primed a schema this process may serve from — and readiness says nothing else at all.
/// </summary>
public class AlvoHealthTests
{
    /// <summary>Liveness answers while the boot has published nothing. The process is up; that is its whole claim.</summary>
    [Fact]
    public async Task Liveness_answers_while_the_boot_is_still_pending()
    {
        await using var world = await AlvoHealthWorld.StartAsync(new AlvoHealthWorldSetup(RunTheBoot: false));

        var liveness = await world.ProbeAsync(AlvoHealth.LivenessPath);

        liveness.Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Readiness is <b>503</b> while the boot is pending — asserted as a status code, which is the only thing
    /// an orchestrator reads.
    /// </summary>
    /// <remarks>
    /// <b>The status code is the fact, and asserting the reported health string instead is how this passes
    /// review and fails in production.</b> ASP.NET Core maps <c>Degraded</c> to <b>200</b> and Kubernetes counts
    /// any 2xx as a passing probe, so a schema gate that reported <c>Degraded</c> would read as "not healthy" in
    /// every log and every dashboard while the pod quietly received traffic with no schema behind it. Changing
    /// <c>AlvoSchemaHealthCheck</c>'s <c>Unhealthy</c> to <c>Degraded</c> turns this fact red and nothing else in
    /// the suite.
    /// </remarks>
    [Fact]
    public async Task Readiness_is_503_while_the_boot_is_pending()
    {
        await using var world = await AlvoHealthWorld.StartAsync(new AlvoHealthWorldSetup(RunTheBoot: false));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        readiness.Body.ShouldBe(nameof(AlvoBootPhase.Pending));
    }

    /// <summary>Readiness is 200 once the boot has primed the schema, and says which phase it is in.</summary>
    [Fact]
    public async Task Readiness_is_200_once_the_boot_is_ready()
    {
        await using var world = await AlvoHealthWorld.StartAsync();

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.OK);
        readiness.Body.ShouldBe(nameof(AlvoBootPhase.Ready));
    }

    /// <summary>
    /// Liveness evaluates <b>no</b> check, so a health check someone adds cannot start killing containers;
    /// the same check does take readiness down, which is where losing traffic is the right consequence.
    /// </summary>
    /// <remarks>
    /// Both halves in one fact on purpose: "liveness stayed 200" is satisfied by a check that never ran at all,
    /// and the 503 beside it is what proves the check was registered, tagged, and evaluated somewhere.
    /// </remarks>
    [Fact]
    public async Task Liveness_runs_no_checks_at_all()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: AlwaysUnhealthy(AlvoHealth.ReadyTag)));

        var liveness = await world.ProbeAsync(AlvoHealth.LivenessPath);
        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        liveness.Status.ShouldBe(HttpStatusCode.OK);
        readiness.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// Readiness evaluates the checks tagged for it and no others — the control for the fact above, which
    /// would pass just as well if readiness evaluated everything ever registered.
    /// </summary>
    [Fact]
    public async Task Readiness_evaluates_only_the_checks_tagged_for_it()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: AlwaysUnhealthy()));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The reason a refused boot publishes really does carry text nobody should read over an anonymous
    /// endpoint — here the descriptor's absolute path, straight out of the runtime's own
    /// <see cref="FileNotFoundException"/>.
    /// </summary>
    /// <remarks>
    /// This is the non-vacuity anchor for the two disclosure facts below. Without it they assert that a body
    /// does not contain a string, which is trivially true of every body if the reason never carried one in the
    /// first place — and it would stay trivially true after someone made <see cref="AlvoBootState.Failure"/>
    /// harmless for a different reason, at which point the guards below would be protecting nothing and nobody
    /// would notice.
    /// </remarks>
    [Fact]
    public async Task The_reason_a_refused_boot_publishes_really_does_carry_sensitive_text()
    {
        var reason = await AlvoHealthWorld.ReasonARefusedBootPublishesAsync("no-such-descriptor.alvo.json");

        reason.ShouldContain(Path.Combine(AppContext.BaseDirectory, "descriptors"));
    }

    /// <summary>
    /// A readiness body reports the phase and <b>never</b> the connection string the refusal reason carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The negative is the fact.</b> Every positive claim about readiness — 503 while pending, 200 when
    /// ready, the right phase in the body — passes happily while the body leaks, because the leak rides in a
    /// field none of them looks at. So this asserts the secret's absence, over a reason the product itself
    /// published (design deviation 59).
    /// </para>
    /// <para>
    /// The reason is produced by a substituted <see cref="IAppliedSchemaStore"/>, which is the shape of the
    /// hazard rather than a stand-in for it: <c>IAppliedSchemaStore</c> is a public port, a third-party driver
    /// is exactly what implements it, and <c>AlvoBootService</c> records whatever message that driver's failure
    /// carried. Neither shipped driver was measured to echo its own connection string, but Alvo's boot has no
    /// way to know which one will, and the phase-only rule costs nothing if none ever does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Readiness_reports_the_phase_and_never_the_connection_string_in_the_reason()
    {
        var reason = await AReasonCarryingAConnectionStringAsync();

        await using var world = await AlvoHealthWorld.StartAsync(new AlvoHealthWorldSetup(RunTheBoot: false));
        world.BootState.Failed(reason);

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Body.ShouldNotContain(SecretPassword);
        readiness.Body.ShouldNotContain(SecretHost);
        readiness.Body.ShouldNotContain("Password");
        readiness.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        readiness.Body.ShouldBe(nameof(AlvoBootPhase.Failed));
    }

    /// <summary>
    /// Nor does the health report carry it — the surface the response writer cannot protect.
    /// </summary>
    /// <remarks>
    /// A check's description travels far beyond the body it is written into:
    /// <c>DefaultHealthCheckService</c> logs it at every evaluation, every <c>IHealthCheckPublisher</c>
    /// receives it, and a host that maps a readiness endpoint of its own with a verbose response writer serves
    /// it to an anonymous caller. So the guard has to be in what the check <em>reports</em>, not only in what
    /// Alvo's own writer chooses to print — and that is a second, independent barrier, which is why it needs a
    /// second fact.
    /// </remarks>
    [Fact]
    public async Task The_readiness_health_report_does_not_carry_the_reason_either()
    {
        var reason = await AReasonCarryingAConnectionStringAsync();

        await using var world = await AlvoHealthWorld.StartAsync(new AlvoHealthWorldSetup(RunTheBoot: false));
        world.BootState.Failed(reason);

        var report = await world.HealthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains(AlvoHealth.ReadyTag),
            TestContext.Current.CancellationToken);

        Everything(report).ShouldNotContain(SecretPassword);
        Everything(report).ShouldNotContain(SecretHost);
        report.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// A host that registered Alvo twice has <b>one of each</b> readiness check, not two under one name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AddCheck</c> is additive, and <c>DefaultHealthCheckService</c> refuses to be constructed at all when
    /// two registrations share a name — so registering either check the obvious way would turn a second
    /// <c>AddAlvo</c>, which every other registration in <c>AddAlvo</c> tolerates, into a host that cannot
    /// answer either probe.
    /// </para>
    /// <para>
    /// The expected set is written out rather than counted, so a <em>new</em> contributor has to be named here
    /// deliberately: an assertion on the count alone, or a "contains", would let a second registration of one
    /// check hide behind the arrival of another.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Registering_Alvo_twice_leaves_one_of_each_readiness_check()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: services => services.AddAlvo()));

        var report = await world.HealthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains(AlvoHealth.ReadyTag),
            TestContext.Current.CancellationToken);

        report.Entries.Keys.ShouldBe(
            [AlvoHealth.DatabaseCheckName, AlvoHealth.SchemaCheckName], ignoreOrder: true);
    }

    /// <summary>
    /// Neither probe's answer may be cached, or an orchestrator reads a phase the process left behind.
    /// </summary>
    /// <remarks>
    /// Nothing configures this: <c>HealthCheckOptions.AllowCachingResponses</c> defaults to
    /// <see langword="false"/> and the framework sends <c>no-store</c> on that default. The fact exists because
    /// the decision not to set it is invisible in the code, and a future option object built with caching
    /// allowed would break a guarantee no other fact here mentions.
    /// </remarks>
    [Fact]
    public async Task Neither_probe_answer_may_be_cached()
    {
        await using var world = await AlvoHealthWorld.StartAsync();

        var liveness = await world.ProbeAsync(AlvoHealth.LivenessPath);
        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        liveness.CacheControl.ShouldNotBeNull().ShouldContain("no-store");
        readiness.CacheControl.ShouldNotBeNull().ShouldContain("no-store");
    }

    /// <summary>
    /// A schema the Data API refuses to route costs the pod its <b>readiness</b> and not its <b>process</b> —
    /// liveness keeps answering 200, which is what <see cref="AlvoHealth.LivenessPath"/> promises nothing anyone
    /// registers can change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An <c>EndpointDataSource</c> is enumerated through the composite of every source the application
    /// registered, so this is not a Data API concern at all.</b> When <c>AlvoEndpointDataSource</c> refused a
    /// hostile schema by <em>throwing</em>, the first request to build the matcher — a probe, typically —
    /// re-raised that refusal for every route in the application, forever: <c>/health/live</c> answered 500, the
    /// container was killed, and it was restart-looped for a schema no restart could fix. Kubernetes' own docs
    /// warn that exactly this mistake cascades under load.
    /// </para>
    /// <para>
    /// Both halves are asserted, because either alone is satisfiable the wrong way: liveness 200 would also hold
    /// if the guard had simply been deleted, and readiness 503 would also hold for a boot that never ran. The
    /// world's own check pins that this boot reached <c>Ready</c>, so the <c>Failed</c> below can only have been
    /// published by the route materialisation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_schema_that_cannot_be_routed_fails_readiness_and_leaves_liveness_answering()
    {
        await using var world = await AlvoHealthWorld.StartAsync(new AlvoHealthWorldSetup(
            Register: services => services.AddSingleton<ISchemaRegistry>(new RegistryShadowingAReservedKey()),
            MapTheDataApi: true));

        var liveness = await world.ProbeAsync(AlvoHealth.LivenessPath);
        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        liveness.Status.ShouldBe(
            HttpStatusCode.OK, "a schema Alvo will not route must not get the container killed");
        readiness.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        readiness.Body.ShouldBe(nameof(AlvoBootPhase.Failed));
        world.BootState.Failure.ShouldNotBeNull().ShouldContain(ReservedQueryKeys.Limit);
    }

    private const string SecretPassword = "pa55w0rd-Sup3rS3cret";

    private const string SecretHost = "alvo-db.internal";

    /// <summary>
    /// The message shape a driver that echoes its own connection string fails with.
    /// </summary>
    private const string LeakyProviderMessage =
        $"Failed to open a connection using 'Host={SecretHost};Port=5432;Username=alvo;"
        + $"Password={SecretPassword};Database=alvo'.";

    /// <summary>The reason the product publishes when the applied-schema store fails that way.</summary>
    private static Task<string> AReasonCarryingAConnectionStringAsync() =>
        AlvoHealthWorld.ReasonARefusedBootPublishesAsync(
            register: services => services.AddSingleton<IAppliedSchemaStore>(
                new StoreThatLeaksItsConnectionString()));

    /// <summary>A check that always fails, optionally tagged.</summary>
    /// <param name="tags">The tags to register it under; none when the fact wants an untagged check.</param>
    private static Action<IServiceCollection> AlwaysUnhealthy(params string[] tags) =>
        services => services.AddHealthChecks()
            .AddCheck("always-unhealthy", () => HealthCheckResult.Unhealthy(), tags);

    /// <summary>Every piece of text a health report carries, so a fact can assert one is not in any of them.</summary>
    private static string Everything(HealthReport report) =>
        string.Join('\n', report.Entries.SelectMany(entry => EverythingIn(entry.Key, entry.Value)));

    private static IEnumerable<string> EverythingIn(string name, HealthReportEntry entry) =>
    [
        name,
        entry.Description ?? string.Empty,
        entry.Exception?.ToString() ?? string.Empty,
        .. entry.Data.Select(item => $"{item.Key}={item.Value}"),
    ];

    /// <summary>
    /// A store that fails the way a driver whose exception message carries its connection string would.
    /// </summary>
    private sealed class StoreThatLeaksItsConnectionString : IAppliedSchemaStore
    {
        public Task<AppliedSchema?> GetCurrentAsync(string project, CancellationToken ct = default) =>
            throw new InvalidOperationException(LeakyProviderMessage);

        public Task SaveAsync(string project, AppliedSchema snapshot, CancellationToken ct = default) =>
            throw new InvalidOperationException(LeakyProviderMessage);
    }
}
