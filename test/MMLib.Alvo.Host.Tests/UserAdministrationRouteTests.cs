using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management.Internal;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>One path, two transports — the second contract.</b>
/// </summary>
/// <remarks>
/// <para>
/// <c>ManagementContractTests</c> holds this for <c>IAlvoManagement</c>, in the core's own suite,
/// where no membership store is composed. This is its sibling for
/// <see cref="IAlvoUserAdministration"/>, and it lives here because the standalone host is where an
/// implementation actually exists: <c>MMLib.Alvo.Identity</c> registers one, so the routes are
/// mapped and can be measured.
/// </para>
/// <para>
/// The risk it closes is the same one: a member reachable in-process by the dashboard and not over
/// HTTP would make <i>"everything the dashboard can do, the API can do"</i> false — quietly, and
/// in the direction that matters, since the dashboard is the thing people will use to discover
/// what the API can do.
/// </para>
/// </remarks>
public class UserAdministrationRouteTests
{
    [Fact]
    public async Task Every_member_of_IAlvoUserAdministration_has_an_http_route()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        var routed = world.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .SelectMany(endpoint => endpoint.Metadata.OfType<ManagementRoute>())
            .Select(route => route.Member)
            .ToHashSet(StringComparer.Ordinal);

        routed.ShouldNotBeEmpty("an empty route table would satisfy the comparison below vacuously");

        var members = typeof(IAlvoUserAdministration).GetMethods()
            .Select(method => method.Name)
            .ToList();

        members.ShouldNotBeEmpty();
        members.ShouldAllBe(
            member => routed.Contains(member),
            "a member with no route is reachable in-process only — which is the divergent path the "
            + "management surface exists to prevent");
    }

    /// <summary>
    /// Every one of them refuses a caller the project admits at no level.
    /// </summary>
    /// <remarks>
    /// Over the wire rather than against the metadata table: metadata and the filter chain are
    /// attached by different calls, so a route that lost its gate still carries the right
    /// attributes. Only a request answers whether the gate runs.
    /// </remarks>
    [Fact]
    public async Task Every_user_route_refuses_a_caller_the_project_names_nowhere()
    {
        await using var world = await AlvoHostWorld.StartAsync();
        var project = "host";

        /* JsonNode.Parse, not the string: JsonNode has an implicit conversion FROM string that
           produces a JSON string VALUE rather than an object, so passing the text directly sends
           `"{...}"` and the endpoint answers 400 at binding — before the gate this test is about
           ever runs. A green-looking wrong measurement, caught once already. */
        var calls = new (HttpMethod Method, string Path, JsonNode? Body)[]
        {
            (HttpMethod.Get, $"/management/projects/{project}/users", null),
            (HttpMethod.Post, $"/management/projects/{project}/users",
                JsonNode.Parse("""{"email":"someone@example.com","roleNames":[]}""")),
            (HttpMethod.Put, $"/management/projects/{project}/users/{Guid.Empty}/roles",
                JsonNode.Parse("""{"roleNames":[]}""")),
            (HttpMethod.Put, $"/management/projects/{project}/users/{Guid.Empty}/tenant",
                JsonNode.Parse("""{"tenant":null}""")),
            (HttpMethod.Put, $"/management/projects/{project}/users/{Guid.Empty}/disabled",
                JsonNode.Parse("""{"disabled":true}""")),
            (HttpMethod.Post, $"/management/projects/{project}/users/{Guid.Empty}/credential-reset", null),
        };

        foreach (var (method, path, body) in calls)
        {
            using var response = await world.SendAsync(method, path, body);

            response.StatusCode.ShouldNotBe(
                HttpStatusCode.NotFound, $"{method} {path} is not mapped at all");
            response.StatusCode.ShouldBe(
                HttpStatusCode.Forbidden,
                $"{method} {path} admitted a caller the project names nowhere: {await response.ReadTextAsync()}");
        }
    }
}
