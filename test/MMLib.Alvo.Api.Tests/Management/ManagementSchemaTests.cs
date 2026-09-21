using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <c>GET {m}/projects/{p}/schema</c> — what the Data API actually serves.
/// </summary>
/// <remarks>
/// <b><c>descriptor</c> versus <c>schema</c> is the same idea one layer down.</b> The descriptor is what the
/// author wrote; the resolved <see cref="Schema.SchemaModel"/> is what survived. Where the two differ is
/// exactly where "declared but not honoured" lives, which is why <c>capabilities</c> sits beside this route.
/// </remarks>
public class ManagementSchemaTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    /// <summary>
    /// The entity set this route answers with is the one the Data API's own routes were generated from.
    /// </summary>
    /// <remarks>
    /// Compared against the <em>live route table</em> rather than against a list written here. A fact that
    /// named the two entities itself would pass just as happily if the endpoint served a schema nothing was
    /// generated from — which is precisely the drift "what is served, not what was declared" is a claim
    /// about.
    /// </remarks>
    [Fact]
    public async Task The_resolved_schema_is_what_the_data_api_routes_are_generated_from()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var schema = await (await world.SendAsync(
            HttpMethod.Get, $"{ManagedFleet.Routes}/schema", _ops)).ReadJsonObjectAsync();
        var served = schema["entities"]!.AsArray()
            .Select(entity => entity!["name"]!.GetValue<string>())
            .Order(StringComparer.Ordinal);

        var routed = world.Routes
            .Select(route => route.Split(' ')[1].Split('/'))
            .Where(segments => segments is [_, "api", _, ..])
            .Select(segments => segments[2])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        routed.ShouldNotBeEmpty("a comparison against an empty route table would prove nothing");
        served.ShouldBe(routed, "the schema endpoint answers with what is served, not with what was declared");
    }

    [Fact]
    public async Task A_project_this_instance_does_not_serve_is_404()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(HttpMethod.Get, "/management/projects/not-mine/schema", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.NotFound,
            "one ISchemaRegistry serves one project, so an unknown name must be refused rather than answered "
            + "with somebody else's schema");
    }
}
