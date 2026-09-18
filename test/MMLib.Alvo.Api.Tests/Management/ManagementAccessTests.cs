using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The gate every management route carries, measured over the wire rather than over the filter.
/// </summary>
/// <remarks>
/// <b>Default-deny is the shipped answer, and it is a property of the descriptor rather than of a stub.</b>
/// A project that declares no <c>access</c> block names nobody, so nobody but the deployment's bootstrap
/// administrator is admitted — which is what the first three facts measure. The fourth applies a descriptor
/// that does name somebody, which is the only way past the gate and the only way the ordering of the two
/// filters is observable: the gate judges whoever the credential resolved to, so a 200 is also the fact that
/// authentication ran first.
/// </remarks>
public class ManagementAccessTests
{
    private static readonly TestApiKey _admin = new("mgmt-admin", ["admin"], ["*:write"]);

    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    /// <summary>
    /// <b>Every route the table carries, not the one path this suite remembers.</b> This is the half
    /// <c>ManagementAccessRouteBuilderExtensions</c> names as owed to the issue that adds the routes: a
    /// route mapped without <c>RequireAlvoManagementAccess</c> is ungated, and the contract facts cannot
    /// see that — they read metadata, and metadata is attached by a different call.
    /// </summary>
    /// <remarks>
    /// Measured, not argued: removing the gate from the one mapped route turns this red and leaves every
    /// other fact in the management suites green.
    /// </remarks>
    [Fact]
    public async Task Every_mapped_management_route_refuses_a_caller_the_project_names_nowhere()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true));

        var addresses = world.ManagementRouteEndpoints().Select(AlvoApiWorld.AddressOf).ToList();

        addresses.ShouldNotBeEmpty("a sweep over an empty route table proves nothing about any route");
        foreach (var (method, path) in addresses)
        {
            var response = await world.SendAsync(method, path, _admin);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path} is not gated");
        }
    }

    [Fact]
    public async Task A_project_that_names_nobody_refuses_an_admin_key_through_the_one_catalogue()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.Forbidden,
            "a management refusal is minted through the one catalogue, not a management-only spelling");
    }

    [Fact]
    public async Task An_anonymous_caller_is_403_rather_than_401()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info");

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "nothing failed authentication: no credential was presented, and the access block refused");
    }

    [Fact]
    public async Task A_presented_credential_that_cannot_be_used_is_401_before_the_gate_is_consulted()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "managed-notes.alvo.json",
            [_ops],
            new AlvoApiWorldSetup(RevokedKeyId: _ops.KeyId, MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _ops);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "this key would have been admitted, so the 401 can only come from the credential itself");
    }

    [Fact]
    public async Task A_caller_the_access_block_names_reaches_info()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "managed-notes.alvo.json", [_ops], new AlvoApiWorldSetup(MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonObjectAsync())["mode"]!.GetValue<string>().ShouldBe("standalone");
    }

    [Fact]
    public async Task Mapping_the_management_api_adds_no_route_under_the_data_api_prefix()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true));

        (await world.SendAsync(HttpMethod.Get, "/api/info")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_host_that_does_not_map_the_management_api_serves_none_of_it()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync("managed-notes.alvo.json", [_ops]);

        (await world.SendAsync(HttpMethod.Get, "/management/info", _ops)).StatusCode.ShouldBe(
            HttpStatusCode.NotFound, "registering Alvo exposes nothing; the endpoint seam is a separate call");
    }
}
