using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// What every route carrying a <c>{project}</c> segment does with a name no project could have.
/// </summary>
/// <remarks>
/// <para>
/// <b>A whitespace segment is the one shape an ordinary unknown-project fact cannot catch.</b> Those facts
/// send a name that is merely wrong, which reaches the boot's <c>ContainsKey</c> miss and earns the named
/// 404. A blank one used to be refused a step earlier by an argument guard, and an
/// <see cref="ArgumentException"/> is family 5 — "an invariant Alvo relies on is broken" — which no
/// management refusal catches and a shipped host renders as a <b>500</b>. That is precisely what
/// <see cref="Management.ManagementProjectNotFoundException"/>'s own remarks say the type exists to prevent.
/// </para>
/// <para>
/// It is post-gate, so it was never a security hole: only a caller the <c>access</c> block already admitted
/// could reach it. It is an agent-first correctness defect — a 500 tells an agent to retry a request whose
/// problem is the request.
/// </para>
/// </remarks>
public class ManagementProjectNameTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    /// <summary>The blank name, percent-encoded so it survives as a route segment rather than an empty one.</summary>
    private const string Blank = "%20";

    [Theory]
    [InlineData("descriptor")]
    [InlineData("revisions")]
    [InlineData("revisions/1")]
    [InlineData("schema")]
    [InlineData("capabilities")]
    public async Task A_blank_project_segment_is_the_named_404_rather_than_a_500(string operation)
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(
            HttpMethod.Get, $"/management/projects/{Blank}/{operation}", _ops);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "a name this instance serves no project by is a 404, whatever the name looks like");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.NotFound);
    }

    /// <summary>The same, on the one parameterised route that carries a body.</summary>
    /// <remarks>
    /// Mapped separately rather than folded into the theory above: it is a <c>POST</c>, and the refusal has
    /// to happen before the body is looked at — otherwise the answer would be the 422 for a body this
    /// request does carry correctly.
    /// </remarks>
    [Fact]
    public async Task A_blank_project_segment_is_the_named_404_on_the_simulator_too()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(
            HttpMethod.Post,
            $"/management/projects/{Blank}/policy/simulate",
            _ops,
            body: new JsonObject
            {
                ["entity"] = "vehicles",
                ["operation"] = "list",
                ["caller"] = new JsonObject
                {
                    ["user"] = Guid.NewGuid().ToString(),
                    ["roles"] = new JsonArray("dispatcher"),
                },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.NotFound);
    }
}
