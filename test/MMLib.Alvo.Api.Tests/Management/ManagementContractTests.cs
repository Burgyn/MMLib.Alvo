using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <b>One path, two transports.</b> The risk in serving the dashboard in-process is an operation reachable
/// through <see cref="IAlvoManagement"/> and not over HTTP; this measures the <em>live endpoint table</em>
/// against the interface's own members, so neither side can move alone.
/// </summary>
/// <remarks>
/// Both sides are asserted non-empty before they are compared. An equality between two empty sets is the one
/// way this suite could pass while measuring nothing at all — and it is exactly the state the route table is
/// in before <c>MapAlvoManagementApi</c> exists.
/// </remarks>
public class ManagementContractTests
{
    [Fact]
    public async Task Every_member_of_IAlvoManagement_has_an_http_route()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true));

        var routed = world.ManagementRoutes().Select(route => route.Member).ToList();

        routed.ShouldNotBeEmpty("an empty route table would satisfy the comparison below vacuously");
        ContractMembers().ShouldNotBeEmpty();
        routed.Order(StringComparer.Ordinal).ShouldBe(
            ContractMembers().Order(StringComparer.Ordinal),
            "a member with no route is reachable in-process only, and a route with no member is a second path");
    }

    [Fact]
    public async Task No_route_stands_for_two_members_and_no_member_for_two_routes()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true));

        var routes = world.ManagementRoutes().ToList();

        routes.ShouldNotBeEmpty();
        routes.Select(route => route.Member).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(routes.Count, "two routes for one member is two paths wearing one name");
    }

    /// <summary>
    /// Every route names an operation the level table has a decision for.
    /// </summary>
    /// <remarks>
    /// <b>This audits the table, not the filter chain.</b> It cannot see whether the route actually carries
    /// the gate — metadata and the filter are attached by different calls, so removing the gate leaves this
    /// green. <c>ManagementAccessTests.Every_mapped_management_route_refuses_a_caller_the_project_names_nowhere</c>
    /// is the fact that answers that, over HTTP.
    /// </remarks>
    [Fact]
    public async Task Every_route_names_an_operation_the_level_table_requires_a_level_for()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true));

        var routes = world.ManagementRoutes().ToList();

        routes.ShouldNotBeEmpty();
        routes.ShouldAllBe(
            route => ManagementOperations.RequiredLevel(route.Operation) != ManagementLevel.None,
            "a route whose operation requires 'None' is a route the gate would admit everyone to");
    }

    [Fact]
    public async Task No_management_route_reaches_the_published_openapi_document()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapOpenApiDocument: true, MapManagementApi: true));

        var paths = (await world.OpenApiDocumentAsync())["paths"]!.AsObject().Select(path => path.Key).ToList();

        paths.ShouldNotBeEmpty();
        paths.ShouldAllBe(
            path => !path.StartsWith("/management", StringComparison.Ordinal),
            "the document Alvo publishes is the generated Data API contract, pinned by a snapshot, a lint "
            + "and an e2e path-set equality; a hand-written admin surface would move all three");
    }

    /// <summary>Every member the contract declares, read off the interface rather than off a list.</summary>
    private static IEnumerable<string> ContractMembers() =>
        typeof(IAlvoManagement).GetMethods().Select(method => method.Name);
}
