using System.Net;
using System.Text.Json.Nodes;

using static MMLib.Alvo.Api.Tests.Management.ManagedExtensions;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary><c>GET {m}/projects/{p}/extensions</c>, the simulator's <c>extensions</c> and the capability report's kinds.</summary>
public sealed class ManagementExtensionsTests
{
    private const string Extensions = ManagedFleet.Routes + "/extensions";

    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    [Fact]
    public async Task A_viewer_reads_what_the_host_registered_in_order()
    {
        await using var world = await ManagedExtensions.StartAsync([_ops]);

        var items = await ItemsAsync(world);

        items.Select(item => item["id"]!.GetValue<string>()).ShouldBe([PlateChanged, EveryWrite, FleetAlerted]);
        var plate = items[0];
        plate["kind"]!.GetValue<string>().ShouldBe("eventHandler");
        plate["stage"]!.GetValue<string>().ShouldBe("postCommit");
        Strings(plate["entities"]).ShouldBe(["vehicles"]);
        Strings(plate["operations"]).ShouldBe(["update"]);
        plate["when"]!.GetValue<string>().ShouldBe("changed(plate)");
        plate["timeout"]!.GetValue<string>().ShouldBe("00:00:30");
        plate["description"]!.GetValue<string>().ShouldBe(PlateChangedDescription);
        plate["typeName"].ShouldBeNull("a type name is served only when Alvo:Extensions:ExposeTypeNames says so");
        Strings(items[1]["entities"]).ShouldBe(["*"]);
        items[2]["event"]!.GetValue<string>().ShouldBe("fleet.alerted");
    }

    [Fact]
    public async Task A_host_that_registers_nothing_answers_an_empty_catalog()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        (await ItemsAsync(world)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_project_is_a_404()
    {
        await using var world = await ManagedExtensions.StartAsync([_ops]);

        using var response = await world.SendAsync(HttpMethod.Get, "/management/projects/nope/extensions", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_before_the_project_is_looked_up()
    {
        await using var world = await ManagedExtensions.StartAsync([_ops]);

        using var existing = await world.SendAsync(HttpMethod.Get, Extensions);
        using var missing = await world.SendAsync(HttpMethod.Get, "/management/projects/nope/extensions");

        existing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "authorization is decided before the project is looked up");
    }

    [Fact]
    public async Task A_simulation_lists_the_extensions_that_run_on_its_entity_and_operation()
    {
        await using var world = await ManagedExtensions.StartAsync([_ops]);

        (await SimulatedAsync(world, "vehicles", "update")).ShouldBe([PlateChanged, EveryWrite]);
        (await SimulatedAsync(world, "depots", "create")).ShouldBe([EveryWrite]);
        (await SimulatedAsync(world, "vehicles", "list")).ShouldBeEmpty("no extension runs on a read in this build");
    }

    [Fact]
    public async Task The_capability_report_honours_the_kind_the_host_registers_after_the_blocks()
    {
        await using var registering = await ManagedExtensions.StartAsync([_ops]);
        await using var plain = await ManagedFleet.StartAsync([_ops]);

        (await HonouredAsync(registering)).ShouldBe(["entities", "auth", "tenancy", "access", "formats", "extensions.eventHandler"]);
        (await HonouredAsync(plain)).ShouldBe(["entities", "auth", "tenancy", "access", "formats"]);
    }

    private static async Task<IReadOnlyList<JsonObject>> ItemsAsync(AlvoApiWorld world)
    {
        var body = await (await world.SendAsync(HttpMethod.Get, Extensions, _ops)).ReadJsonObjectAsync();

        return [.. body["items"].ShouldBeOfType<JsonArray>().Select(item => item!.AsObject())];
    }

    private static async Task<IReadOnlyList<string>> SimulatedAsync(AlvoApiWorld world, string entity, string operation)
    {
        var body = new JsonObject
        {
            ["entity"] = entity,
            ["operation"] = operation,
            ["caller"] = new JsonObject { ["user"] = Guid.NewGuid().ToString(), ["roles"] = new JsonArray("dispatcher") },
        };
        var verdict = await (await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/policy/simulate", _ops, body: body))
            .ReadJsonObjectAsync();

        return [.. verdict["extensions"].ShouldBeOfType<JsonArray>().Select(item => item!["id"]!.GetValue<string>())];
    }

    private static async Task<IReadOnlyList<string>> HonouredAsync(AlvoApiWorld world) =>
        Strings((await (await world.SendAsync(HttpMethod.Get, $"{ManagedFleet.Routes}/capabilities", _ops)).ReadJsonObjectAsync())["honoured"]);

    private static IReadOnlyList<string> Strings(JsonNode? array) =>
        [.. array.ShouldBeOfType<JsonArray>().Select(item => item!.GetValue<string>())];
}
