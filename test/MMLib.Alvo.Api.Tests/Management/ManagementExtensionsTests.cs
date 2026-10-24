using System.Net;
using System.Text.Json.Nodes;

using static MMLib.Alvo.Api.Tests.Management.ManagedExtensions;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary><c>GET {m}/projects/{p}/extensions</c>, the simulator's <c>extensions</c> and the capability report's kinds.</summary>
public sealed class ManagementExtensionsTests
{
    private const string Extensions = ManagedFleet.Routes + "/extensions";

    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);
    private static readonly TestApiKey _dev = new("mgmt-dev", ["dispatcher"], ["*:write"]);

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

    [Fact]
    public async Task Removing_a_bound_field_is_refused_naming_the_extension()
    {
        await using var world = await ManagedExtensions.StartAsync([_dev]);
        var descriptor = await CurrentAsync(world);
        WithoutPlate(descriptor);

        var violation = await RefusedAsync(world, descriptor, allowDestructive: true);

        violation["pointer"]!.GetValue<string>().ShouldBe("/entities/vehicles/fields/plate");
        violation["message"]!.GetValue<string>().ShouldContain($"'{PlateChanged}' (eventHandler, in this host)");
        violation["fixSuggestion"]!.GetValue<string>().ShouldBe("Remove the registration and redeploy first, or keep the field.");
        (await ManagementApplyWorld.RevisionAsync(world, _dev)).ShouldBe(1);
    }

    [Fact]
    public async Task An_unbinding_apply_on_a_stale_revision_is_answered_with_the_refusal_not_the_precondition()
    {
        await using var world = await ManagedExtensions.StartAsync([_dev]);
        var descriptor = await CurrentAsync(world);
        WithoutPlate(descriptor);

        using var response = await ManagementApplyWorld.ApplyAsync(
            world, _dev, descriptor.ToJsonString(), ifMatch: "\"7\"", allowDestructive: true);

        response.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity, "the guard runs right after validation, before the revision is checked");
        (await response.ReadJsonObjectAsync())["violations"]!.AsArray()
            .Select(violation => violation!["pointer"]!.GetValue<string>())
            .ShouldContain("/entities/vehicles/fields/plate");
    }

    [Fact]
    public async Task Excluding_a_bound_field_from_the_event_images_is_refused_naming_the_extension()
    {
        await using var world = await ManagedExtensions.StartAsync([_dev]);
        var descriptor = await CurrentAsync(world);
        descriptor["entities"]!["vehicles"]!["events"] = new JsonObject { ["exclude"] = new JsonArray("plate") };

        var violation = await RefusedAsync(world, descriptor);

        violation["pointer"]!.GetValue<string>().ShouldBe("/entities/vehicles/events/exclude/0");
        violation["message"]!.GetValue<string>().ShouldContain($"'{PlateChanged}'");
    }

    [Fact]
    public async Task A_dry_run_removing_a_bound_entity_is_refused_at_the_entity()
    {
        await using var world = await ManagedExtensions.StartAsync([_dev]);
        var descriptor = await CurrentAsync(world);
        descriptor["entities"]!.AsObject().Remove("vehicles");

        var violation = await RefusedAsync(world, descriptor, allowDestructive: true, query: "?dryRun=true");

        violation["pointer"]!.GetValue<string>().ShouldBe("/entities/vehicles");
    }

    [Fact]
    public async Task An_apply_that_keeps_what_every_extension_reads_is_applied()
    {
        await using var world = await ManagedExtensions.StartAsync([_dev]);
        var descriptor = await CurrentAsync(world);
        descriptor["entities"]!["vehicles"]!["fields"]!["nickname"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 };

        using var response = await ManagementApplyWorld.ApplyAsync(world, _dev, descriptor.ToJsonString(), ifMatch: "\"1\"");

        response.IsSuccessStatusCode.ShouldBeTrue();
        (await ManagementApplyWorld.RevisionAsync(world, _dev)).ShouldBe(2);
    }

    /// <summary>
    /// Drops <c>plate</c> and adds a field in its place: the fixture's <c>vehicles</c> holds <c>plate</c> alone, and an
    /// entity with no field is the schema's refusal, not the guard's.
    /// </summary>
    private static void WithoutPlate(JsonNode descriptor)
    {
        var fields = descriptor["entities"]!["vehicles"]!["fields"]!.AsObject();
        fields.Remove("plate");
        fields["vin"] = new JsonObject { ["type"] = "string", ["maxLength"] = 17 };
    }

    private static async Task<JsonNode> CurrentAsync(AlvoApiWorld world) =>
        JsonNode.Parse(await ManagementApplyWorld.CurrentAsync(world, _dev))!;

    /// <summary>The refusal's one pointed violation; the compiler's own sentences travel beside it at the empty pointer.</summary>
    private static async Task<JsonObject> RefusedAsync(AlvoApiWorld world, JsonNode descriptor, bool allowDestructive = false, string query = "")
    {
        using var response = await ManagementApplyWorld.ApplyAsync(
            world, _dev, descriptor.ToJsonString(), ifMatch: "\"1\"", allowDestructive: allowDestructive, query: query);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        return (await response.ReadJsonObjectAsync())["violations"]!.AsArray()
            .Select(violation => violation!.AsObject())
            .Where(violation => violation["pointer"]!.GetValue<string>().Length > 0)
            .ShouldHaveSingleItem();
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
