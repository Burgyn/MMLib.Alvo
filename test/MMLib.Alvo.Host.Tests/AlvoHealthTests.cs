using MMLib.Alvo.Api;
using MMLib.Alvo.Migrations;
using System.Net;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The standalone host's probes, which are now the core's: it maps <c>MapAlvoHealth</c> rather than a liveness
/// route of its own, so the route a container's <c>healthcheck</c> calls is the one the framework owns.
/// </summary>
public class AlvoHealthTests
{
    /// <summary>
    /// Readiness answers an unauthenticated probe, and answers 200 over a host that really booted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on <em>this</em> host rather than on an embedded one, because the claim is about the standalone
    /// image: it is the host that configures a credential and a context filter, so "no credential needed" is
    /// only worth asserting where there is a credential to omit —
    /// <c>AlvoHostBootTests.Liveness_answers_an_unauthenticated_probe</c> is the same claim for the other probe.
    /// </para>
    /// <para>
    /// The body is asserted too, and that is the delegation half of the fact: a host that had kept mapping a
    /// liveness route of its own under the readiness path would answer 200 with <c>Healthy</c>, not with the
    /// boot phase.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Readiness_answers_an_unauthenticated_probe_over_a_booted_host()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoHealth.ReadinessPath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldBe(nameof(AlvoBootPhase.Ready));
    }
}
