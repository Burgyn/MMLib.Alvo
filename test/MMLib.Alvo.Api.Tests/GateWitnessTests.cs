using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Migrations;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Acceptance (6), issue #184: no convention leaves a generated route without Alvo's authorization filter — whichever
/// builder it was attached to and in whichever order it ran. One that removes the filter before it is composed fails the
/// route-table build and readiness reports it; one that clears the list after it is composed changes nothing.
/// </summary>
public sealed class GateWitnessTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    [Fact]
    public async Task Every_generated_endpoint_composes_alvos_gate_into_its_request_delegate()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        var witnesses = world.Endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<AlvoGateWitness>()).ToList();

        witnesses.Count.ShouldBeGreaterThan(0);
        witnesses.ShouldAllBe(witness => witness != null && witness.Compositions > 0);
    }

    [Fact]
    public async Task A_convention_removing_alvos_filter_fails_the_route_table_build() =>
        await ShouldFailTheBuildAsync(new AlvoApiWorldSetup(ConfigureDataApiRoutes: routes => routes.Add(endpoint => endpoint.FilterFactories.Clear())));

    // Measured on .NET 10: a finally convention — the route's or a group's — runs after the request delegate, and with it
    // the filter pipeline, has been composed, so clearing FilterFactories there removes nothing. These two facts pin that
    // the gate still answers; the witness check stays the order-independent guarantee should the framework's order change.
    [Fact]
    public async Task A_finally_convention_clearing_the_filter_list_cannot_remove_alvos_gate() =>
        await ShouldStillGateAsync(new AlvoApiWorldSetup(ConfigureDataApiRoutes: routes => routes.Finally(endpoint => endpoint.FilterFactories.Clear())));

    [Fact]
    public async Task A_group_finally_convention_clearing_the_filter_list_cannot_remove_alvos_gate() =>
        await ShouldStillGateAsync(new AlvoApiWorldSetup(
            RouteGroupPrefix: "/v1",
            ConfigureRouteGroup: group => group.Finally(endpoint => endpoint.FilterFactories.Clear())),
            "/v1/api/owners");

    [Fact]
    public async Task A_convention_rebuilding_the_filter_list_with_the_same_filters_still_fails()
    {
        await ShouldFailTheBuildAsync(new AlvoApiWorldSetup(ConfigureDataApiRoutes: routes => routes.Add(endpoint =>
        {
            var kept = endpoint.FilterFactories.Select(factory => (Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate>)((context, next) => next)).ToList();
            endpoint.FilterFactories.Clear();
            foreach (var factory in kept)
            {
                endpoint.FilterFactories.Add(factory);
            }
        })));
    }

    [Fact]
    public async Task A_convention_adding_a_filter_around_alvos_still_builds_and_still_gates() =>
        await ShouldStillGateAsync(new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes => routes.AddEndpointFilter((context, next) => next(context))));

    private static async Task ShouldStillGateAsync(AlvoApiWorldSetup setup, string path = "/api/owners")
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], setup);

        using var anonymousWrite = await world.SendAsync(HttpMethod.Post, path, body: new JsonObject { ["name"] = "Ann" });

        world.Services.GetRequiredService<AlvoBootState>().Phase.ShouldBe(AlvoBootPhase.Ready);
        anonymousWrite.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Alvo's gate still runs");
    }

    private static async Task ShouldFailTheBuildAsync(AlvoApiWorldSetup setup, string path = "/api/owners")
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], setup);

        using var response = await world.SendAsync(HttpMethod.Get, path, _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a route table that could not be gated routes nothing");
        var boot = world.Services.GetRequiredService<AlvoBootState>();
        boot.Phase.ShouldBe(AlvoBootPhase.Failed, "an orchestrator has to be able to drain this pod");
        boot.Failure.ShouldNotBeNull().ShouldContain("removed Alvo's authorization filter");
    }
}
