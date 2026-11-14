using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Management;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <b>One path, two transports — the accounts port.</b> <c>ManagementContractTests</c>' counterpart for
/// <see cref="IAlvoAccountAdministration"/>, the <c>UserAdministrationRouteTests</c> shape: the core maps the route only
/// when an implementation is registered, so a stand-in is registered here and <c>MMLib.Alvo.Accounts</c> is not needed.
/// </summary>
public sealed class AccountAdministrationRouteTests
{
    private static readonly TestApiKey _stranger = new("mgmt-stranger", ["admin"], ["*:write"]);

    [Fact]
    public async Task Every_member_has_exactly_one_route()
    {
        await using var world = await StartAsync(withAccounts: true);

        var routed = AccountRoutes(world).Select(route => route.Member).ToList();
        var members = typeof(IAlvoAccountAdministration).GetMethods().Select(method => method.Name).ToList();

        members.ShouldNotBeEmpty("an empty member list would satisfy the comparison below vacuously");
        routed.Order(StringComparer.Ordinal).ShouldBe(
            members.Order(StringComparer.Ordinal),
            "a member with no route is reachable in-process only, and two routes for one member are two paths");
    }

    [Fact]
    public async Task Every_route_names_an_operation_the_level_table_puts_at_admin()
    {
        await using var world = await StartAsync(withAccounts: true);

        var routes = AccountRoutes(world).ToList();

        routes.ShouldNotBeEmpty();
        routes.ShouldAllBe(route => route.Operation == ManagementOperation.ManageAccounts);
        ManagementOperations.RequiredLevel(ManagementOperation.ManageAccounts).ShouldBe(ManagementLevel.Admin);
    }

    /// <summary>Over the wire: a caller the project names nowhere is refused, so the route really carries its gate.</summary>
    [Fact]
    public async Task The_route_refuses_a_caller_the_project_names_nowhere()
    {
        await using var world = await StartAsync(withAccounts: true);

        using var response = await world.SendAsync(
            HttpMethod.Put,
            $"/management/projects/vehicle-registry/accounts/{Guid.NewGuid()}/disabled",
            _stranger,
            body: new JsonObject { ["disabled"] = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.ReadTextAsync());
    }

    [Fact]
    public async Task No_route_reaches_the_published_openapi_document()
    {
        await using var world = await StartAsync(withAccounts: true, openApi: true);

        var paths = (await world.OpenApiDocumentAsync())["paths"]!.AsObject().Select(path => path.Key).ToList();

        AccountRoutes(world).ShouldNotBeEmpty("the route must exist for its absence from the document to mean anything");
        paths.ShouldNotBeEmpty();
        paths.ShouldAllBe(path => !path.Contains("/accounts/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_accounts_registered_the_route_is_404()
    {
        await using var world = await StartAsync(withAccounts: false);

        using var response = await world.SendAsync(
            HttpMethod.Put,
            $"/management/projects/vehicle-registry/accounts/{Guid.NewGuid()}/disabled",
            _stranger,
            body: new JsonObject { ["disabled"] = true });

        AccountRoutes(world).ShouldBeEmpty();
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a deployment without sign-in has no account to disable");
    }

    private static Task<AlvoApiWorld> StartAsync(bool withAccounts, bool openApi = false) =>
        AlvoApiWorld.VehicleRegistryAsync(
            [_stranger],
            new AlvoApiWorldSetup(
                MapOpenApiDocument: openApi,
                MapManagementApi: true,
                ConfigureServicesAfterAlvo: services =>
                {
                    if (withAccounts)
                    {
                        services.AddSingleton<IAlvoAccountAdministration, NoAccounts>();
                    }
                }));

    private static IEnumerable<MMLib.Alvo.Management.Internal.ManagementRoute> AccountRoutes(AlvoApiWorld world) =>
        world.ManagementRouteEndpoints()
            .Where(endpoint => endpoint.RoutePattern.RawText!.Contains("/accounts/", StringComparison.Ordinal))
            .Select(endpoint => endpoint.Metadata.GetMetadata<MMLib.Alvo.Management.Internal.ManagementRoute>()!);

    /// <summary>A stand-in that knows no account; the facts here are about the route, never about the store.</summary>
    private sealed class NoAccounts : IAlvoAccountAdministration
    {
        public Task<bool> SetDisabledAsync(UserId account, bool disabled, CancellationToken ct) => Task.FromResult(false);
    }
}
